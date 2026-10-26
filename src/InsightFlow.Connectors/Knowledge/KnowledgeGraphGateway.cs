using InsightFlow.Domain.Knowledge;
using MCC.Foundation.Neo4JHelper.Models;
using MCC.Foundation.Neo4JHelper.Neo4JHelper;
using Microsoft.Extensions.Logging;

namespace InsightFlow.Connectors.Knowledge;

/// <summary>Everything needed to open one connection to a tenant's Neo4j. The password is the resolved secret, never stored.</summary>
public sealed record KnowledgeConnectionSettings(string Uri, string User, string Password, string Database, bool UseAura, bool Encrypted);

/// <summary>A table the graph matched to a question, with how well, and the columns that matched.</summary>
public sealed record GraphTableHit(Guid TableId, string Name, double Score, IReadOnlyList<string> MatchedColumns);

public sealed record KnowledgeTestResult(bool Succeeded, string Message, string? ServerAgent);

/// <summary>What was written by <see cref="IKnowledgeGraphGateway.ApplyAsync"/>.</summary>
public sealed record KnowledgeSeedCounts(int Tables, int Columns, int Relationships, int Lineage);

/// <summary>What is in the graph right now.</summary>
public sealed record KnowledgeGraphSummary(int Tables, int Columns, int Relationships, int Lineage);

/// <summary>
/// The one place that talks to Neo4j (through <c>MCC.Foundation.Neo4JHelper</c>). A connection is opened per call from the
/// tenant's own settings and disposed afterwards, because every tenant brings its own server. All Cypher here is fixed
/// text; names, types and descriptions only ever travel as parameters (never concatenated, and never model-written Cypher).
/// </summary>
public interface IKnowledgeGraphGateway
{
    Task<KnowledgeTestResult> TestAsync(KnowledgeConnectionSettings settings, CancellationToken cancellationToken);

    /// <summary>Replaces the tenant's part of the graph with <paramref name="plan"/> in one transaction (nothing is half-written on failure).</summary>
    Task<KnowledgeSeedCounts> ApplyAsync(KnowledgeConnectionSettings settings, Guid tenantId, KnowledgeGraphPlan plan, CancellationToken cancellationToken);

    Task<KnowledgeGraphSummary> SummarizeAsync(KnowledgeConnectionSettings settings, Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Tables whose database, name, description or columns mention any of the words, best first. The words are parameters; the Cypher is fixed.</summary>
    Task<IReadOnlyList<GraphTableHit>> FindTablesAsync(KnowledgeConnectionSettings settings, Guid tenantId, IReadOnlyList<string> words, int limit, CancellationToken cancellationToken);

    /// <summary>Every relationship in the tenant's graph as a table-to-table edge, for finding how tables can be joined.</summary>
    Task<IReadOnlyList<JoinEdge>> LoadJoinEdgesAsync(KnowledgeConnectionSettings settings, Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Thrown when Neo4j cannot be reached or rejects a request. The message is safe to show to the administrator.</summary>
public sealed class KnowledgeGraphException : Exception
{
    public KnowledgeGraphException()
    {
    }

    public KnowledgeGraphException(string message)
        : base(message)
    {
    }

    public KnowledgeGraphException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed partial class Neo4jKnowledgeGraphGateway(ILogger<Neo4jKnowledgeGraphGateway> logger) : IKnowledgeGraphGateway
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(90);

    private const string ClearCypher =
        "MATCH (n) WHERE (n:Database OR n:Table OR n:Column) AND n.tenantId = $tenantId DETACH DELETE n";

    private const string DatabasesCypher =
        "UNWIND $databases AS d MERGE (x:Database {key: d.key}) SET x.name = d.name, x.description = d.description, x.connectionId = d.connectionId, x.tenantId = $tenantId";

    private const string DatabaseTablesCypher =
        "UNWIND $links AS l MATCH (d:Database {key: l.database}), (t:Table {key: l.table}) MERGE (d)-[:HAS_TABLE]->(t)";

    private const string TablesCypher =
        "UNWIND $tables AS t MERGE (x:Table {key: t.key}) SET x.name = t.name, x.tenantId = $tenantId, x.tableId = t.tableId, x.description = t.description, x.source = t.source, x.rowCount = t.rowCount";

    private const string ColumnsCypher =
        "UNWIND $columns AS c MATCH (t:Table {key: c.tableKey}) MERGE (x:Column {key: c.key}) "
        + "SET x.name = c.name, x.dataType = c.dataType, x.description = c.description, x.isPrimaryKey = c.isPrimaryKey, x.tableName = c.tableName, x.tenantId = $tenantId "
        + "MERGE (t)-[:HAS_COLUMN]->(x)";

    private const string LineageCypher =
        "UNWIND $lineage AS l MATCH (a:Table {key: l.child}), (b:Table {key: l.parent}) MERGE (a)-[:DERIVED_FROM]->(b)";

    private const string RelationshipsCypher =
        "UNWIND $relationships AS r MATCH (a:Column {key: r.from}), (b:Column {key: r.to}) "
        + "MERGE (a)-[x:RELATED_TO]->(b) SET x.kind = r.kind, x.confidence = r.confidence, x.reason = r.reason, x.tenantId = $tenantId, "
        + "x.groupId = r.groupId, x.groupSize = r.groupSize, x.audit = r.audit";

    // A table-level view of the same joins, so the graph reads as tables joined to tables in a viewer. Audit relationships (who created or
    // changed a row) are left out, and the query code keeps reading the column-level RELATED_TO edges.
    private const string JoinsToCypher =
        "MATCH (a:Table {tenantId: $tenantId})-[:HAS_COLUMN]->(ca:Column)-[r:RELATED_TO]->(cb:Column)<-[:HAS_COLUMN]-(b:Table {tenantId: $tenantId}) "
        + "WHERE a <> b AND coalesce(r.audit, false) = false "
        + "WITH a, b, collect(DISTINCT ca.name + ' = ' + cb.name) AS pairs, count(r) AS n "
        + "MERGE (a)-[j:JOINS_TO]->(b) SET j.columns = pairs[..10], j.relationships = n, j.tenantId = $tenantId";

    private const string SummaryCypher =
        "OPTIONAL MATCH (t:Table {tenantId: $tenantId}) WITH count(t) AS tables "
        + "OPTIONAL MATCH (c:Column {tenantId: $tenantId}) WITH tables, count(c) AS columns "
        + "OPTIONAL MATCH (:Column {tenantId: $tenantId})-[r:RELATED_TO]->(:Column) WITH tables, columns, count(r) AS relationships "
        + "OPTIONAL MATCH (:Table {tenantId: $tenantId})-[l:DERIVED_FROM]->(:Table) "
        + "RETURN tables, columns, relationships, count(l) AS lineage";

    public async Task<KnowledgeTestResult> TestAsync(KnowledgeConnectionSettings settings, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TestTimeout);
        try
        {
            await using var neo4j = Open(settings);
            var connectivity = await neo4j.VerifyConnectivityAsync(timeout.Token).WaitAsync(timeout.Token);
            if (!connectivity.IsSuccess)
            {
                return new KnowledgeTestResult(false, Clean(connectivity.Message, "Could not connect to Neo4j."), null);
            }

            var info = await neo4j.GetServerInfoAsync(timeout.Token).WaitAsync(timeout.Token);
            return new KnowledgeTestResult(true, "Connected to Neo4j.", info.IsSuccess ? info.Agent : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KnowledgeTestResult(false, "Timed out after 15 seconds. Check the address, port and firewall.", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTestFailed(logger, ex.GetType().Name);
            return new KnowledgeTestResult(false, Clean(ex.Message, "Could not connect to Neo4j."), null);
        }
    }

    public async Task<KnowledgeSeedCounts> ApplyAsync(
        KnowledgeConnectionSettings settings, Guid tenantId, KnowledgeGraphPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var tenant = tenantId.ToString("N");

        var tables = plan.Tables.Select(t => new Dictionary<string, object?>
        {
            ["key"] = TableKey(tenant, t.Id),
            ["name"] = t.Name,
            ["tableId"] = t.Id.ToString("D"),
            ["description"] = t.Description,
            ["source"] = t.Source,
            ["rowCount"] = t.RowCount,
        }).ToList();

        var databases = (plan.Databases ?? []).Select(d => new Dictionary<string, object?>
        {
            ["key"] = DatabaseKey(tenant, d.ConnectionId, d.Name),
            ["name"] = d.Name,
            ["description"] = d.Description,
            ["connectionId"] = d.ConnectionId?.ToString("D"),
        }).ToList();

        // Hierarchy: Database -> Table -> Column. A table with a database name hangs under that database.
        var databaseLinks = plan.Tables
            .Where(t => !string.IsNullOrEmpty(t.DatabaseName))
            .Select(t => new Dictionary<string, object?> { ["database"] = DatabaseKey(tenant, t.ConnectionId, t.DatabaseName!), ["table"] = TableKey(tenant, t.Id) })
            .ToList();

        var tableKeys = plan.Tables.ToDictionary(t => t.Name, t => TableKey(tenant, t.Id), StringComparer.OrdinalIgnoreCase);
        var columns = plan.Tables.SelectMany(t => t.Columns.Select(c => new Dictionary<string, object?>
        {
            ["key"] = ColumnKey(tableKeys[t.Name], c.Name),
            ["tableKey"] = tableKeys[t.Name],
            ["tableName"] = t.Name,
            ["name"] = c.Name,
            ["dataType"] = c.DataType,
            ["description"] = c.Description,
            ["isPrimaryKey"] = c.IsPrimaryKey,
        })).ToList();

        var lineage = plan.Lineage
            .Where(l => tableKeys.ContainsKey(l.Table) && tableKeys.ContainsKey(l.ParentTable))
            .Select(l => new Dictionary<string, object?> { ["child"] = tableKeys[l.Table], ["parent"] = tableKeys[l.ParentTable] })
            .ToList();

        var relationships = plan.Relationships
            .Where(r => tableKeys.ContainsKey(r.FromTable) && tableKeys.ContainsKey(r.ToTable))
            .Select(r => new Dictionary<string, object?>
            {
                ["from"] = ColumnKey(tableKeys[r.FromTable], r.FromColumn),
                ["to"] = ColumnKey(tableKeys[r.ToTable], r.ToColumn),
                ["kind"] = r.Kind.ToString(),
                ["confidence"] = r.Confidence,
                ["reason"] = r.Reason,
                // Pairs of one composite key share a group id: a consumer must join on all of them together, not on one.
                ["groupId"] = r.GroupId?.ToString("N"),
                ["groupSize"] = r.GroupSize,
                ["audit"] = AuditColumns.IsAuditRelationship(r.FromColumn, r.ToColumn),
            })
            .ToList();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApplyTimeout);
        try
        {
            await using var neo4j = Open(settings);
            await EnsureConstraintsAsync(neo4j, settings.Database, timeout.Token);

            await neo4j.ExecuteInTransactionAsync<int>(
                async (runner, ct) =>
                {
                    await RunAsync(runner, ClearCypher, new Dictionary<string, object?> { ["tenantId"] = tenant });
                    await RunAsync(runner, TablesCypher, new Dictionary<string, object?> { ["tenantId"] = tenant, ["tables"] = tables });
                    await RunAsync(runner, DatabasesCypher, new Dictionary<string, object?> { ["tenantId"] = tenant, ["databases"] = databases });
                    await RunAsync(runner, DatabaseTablesCypher, new Dictionary<string, object?> { ["links"] = databaseLinks });
                    await RunAsync(runner, ColumnsCypher, new Dictionary<string, object?> { ["tenantId"] = tenant, ["columns"] = columns });
                    await RunAsync(runner, LineageCypher, new Dictionary<string, object?> { ["lineage"] = lineage });
                    await RunAsync(runner, RelationshipsCypher, new Dictionary<string, object?> { ["tenantId"] = tenant, ["relationships"] = relationships });
                    await RunAsync(runner, JoinsToCypher, new Dictionary<string, object?> { ["tenantId"] = tenant });
                    return 0;
                },
                new Neo4jTransactionRequestModel { AccessMode = Neo4jAccessMode.Write, Database = settings.Database },
                timeout.Token).WaitAsync(timeout.Token);

            LogApplied(logger, tables.Count, columns.Count, relationships.Count);
            return new KnowledgeSeedCounts(tables.Count, columns.Count, relationships.Count, lineage.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new KnowledgeGraphException("Neo4j did not finish within 90 seconds. Nothing was changed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not KnowledgeGraphException)
        {
            LogApplyFailed(logger, ex.GetType().Name);
            throw new KnowledgeGraphException(Clean(ex.Message, "Neo4j rejected the update."), ex);
        }
    }

    public async Task<KnowledgeGraphSummary> SummarizeAsync(KnowledgeConnectionSettings settings, Guid tenantId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TestTimeout);
        try
        {
            await using var neo4j = Open(settings);
            var result = await neo4j.ExecuteReadAsync(
                new Neo4jQueryRequestModel
                {
                    Query = SummaryCypher,
                    Parameters = new Dictionary<string, object?> { ["tenantId"] = tenantId.ToString("N") },
                    AccessMode = Neo4jAccessMode.Read,
                    Database = settings.Database,
                },
                timeout.Token).WaitAsync(timeout.Token);
            if (!result.IsSuccess)
            {
                throw new KnowledgeGraphException(Clean(result.Message, "Neo4j could not be queried."));
            }

            var row = result.Records.FirstOrDefault();
            return row is null
                ? new KnowledgeGraphSummary(0, 0, 0, 0)
                : new KnowledgeGraphSummary(Count(row, "tables"), Count(row, "columns"), Count(row, "relationships"), Count(row, "lineage"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new KnowledgeGraphException("Neo4j did not answer within 15 seconds.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not KnowledgeGraphException)
        {
            throw new KnowledgeGraphException(Clean(ex.Message, "Neo4j could not be queried."), ex);
        }
    }

    private const string FindTablesCypher =
        "UNWIND $words AS word "
        + "MATCH (t:Table {tenantId: $tenantId}) "
        + "OPTIONAL MATCH (d:Database)-[:HAS_TABLE]->(t) "
        + "OPTIONAL MATCH (t)-[:HAS_COLUMN]->(c:Column) WHERE toLower(c.name) CONTAINS word OR toLower(coalesce(c.description, '')) CONTAINS word "
        + "WITH t, d, word, collect(DISTINCT c.name) AS cols "
        + "WITH t, word, cols, "
        + "(CASE WHEN toLower(t.name) CONTAINS word THEN 3 ELSE 0 END "
        + "+ CASE WHEN toLower(coalesce(t.description, '')) CONTAINS word THEN 2 ELSE 0 END "
        + "+ CASE WHEN d IS NOT NULL AND (toLower(d.name) CONTAINS word OR toLower(coalesce(d.description, '')) CONTAINS word) THEN 1 ELSE 0 END "
        + "+ CASE WHEN size(cols) > 0 THEN 1 + toInteger(CASE WHEN size(cols) > 3 THEN 3 ELSE size(cols) END) ELSE 0 END) AS points "
        + "WITH t, sum(points) AS score, collect(cols) AS lists "
        + "WHERE score > 0 "
        + "RETURN t.tableId AS tableId, t.name AS name, score, reduce(all = [], l IN lists | all + l)[..12] AS matched "
        + "ORDER BY score DESC, name LIMIT $limit";

    private const string JoinEdgesCypher =
        "MATCH (a:Table {tenantId: $tenantId})-[:HAS_COLUMN]->(ca:Column)-[r:RELATED_TO]->(cb:Column)<-[:HAS_COLUMN]-(b:Table {tenantId: $tenantId}) "
        + "RETURN a.tableId AS fromTable, ca.name AS fromColumn, b.tableId AS toTable, cb.name AS toColumn, r.kind AS kind, r.confidence AS confidence, r.groupId AS groupId";

    public async Task<IReadOnlyList<GraphTableHit>> FindTablesAsync(
        KnowledgeConnectionSettings settings, Guid tenantId, IReadOnlyList<string> words, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0)
        {
            return [];
        }

        var rows = await ReadAsync(
            settings,
            FindTablesCypher,
            new Dictionary<string, object?> { ["tenantId"] = tenantId.ToString("N"), ["words"] = words.ToList(), ["limit"] = Math.Clamp(limit, 1, 50) },
            cancellationToken);
        var hits = new List<GraphTableHit>();
        foreach (var row in rows)
        {
            if (row.GetValueOrDefault("tableId") is string id && Guid.TryParse(id, out var tableId))
            {
                var matched = row.GetValueOrDefault("matched") is System.Collections.IEnumerable list
                    ? list.Cast<object?>().Select(o => o?.ToString()).OfType<string>().ToList()
                    : [];
                hits.Add(new GraphTableHit(
                    tableId,
                    row.GetValueOrDefault("name")?.ToString() ?? string.Empty,
                    Convert.ToDouble(row.GetValueOrDefault("score") ?? 0, System.Globalization.CultureInfo.InvariantCulture),
                    matched));
            }
        }

        return hits;
    }

    public async Task<IReadOnlyList<JoinEdge>> LoadJoinEdgesAsync(KnowledgeConnectionSettings settings, Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(settings, JoinEdgesCypher, new Dictionary<string, object?> { ["tenantId"] = tenantId.ToString("N") }, cancellationToken);
        var edges = new List<JoinEdge>();
        foreach (var row in rows)
        {
            if (row.GetValueOrDefault("fromTable") is string from && Guid.TryParse(from, out var fromId)
                && row.GetValueOrDefault("toTable") is string to && Guid.TryParse(to, out var toId)
                && row.GetValueOrDefault("fromColumn") is string fromColumn && row.GetValueOrDefault("toColumn") is string toColumn
                && Enum.TryParse<RelationshipKind>(row.GetValueOrDefault("kind")?.ToString(), ignoreCase: true, out var kind))
            {
                Guid? group = row.GetValueOrDefault("groupId") is string g && Guid.TryParseExact(g, "N", out var parsed) ? parsed : null;
                edges.Add(new JoinEdge(fromId, fromColumn, toId, toColumn, kind, Convert.ToDouble(row.GetValueOrDefault("confidence") ?? 1, System.Globalization.CultureInfo.InvariantCulture), group));
            }
        }

        return edges;
    }

    /// <summary>One read-only query with a time limit; the Cypher is always a fixed constant from this class.</summary>
    private async Task<IReadOnlyList<Dictionary<string, object?>>> ReadAsync(
        KnowledgeConnectionSettings settings, string cypher, Dictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TestTimeout);
        try
        {
            await using var neo4j = Open(settings);
            var result = await neo4j.ExecuteReadAsync(
                new Neo4jQueryRequestModel { Query = cypher, Parameters = parameters, AccessMode = Neo4jAccessMode.Read, Database = settings.Database },
                timeout.Token).WaitAsync(timeout.Token);
            if (!result.IsSuccess)
            {
                throw new KnowledgeGraphException(Clean(result.Message, "Neo4j could not be queried."));
            }

            return [.. result.Records];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new KnowledgeGraphException("Neo4j did not answer within 15 seconds.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not KnowledgeGraphException)
        {
            LogReadFailed(logger, ex.GetType().Name);
            throw new KnowledgeGraphException(Clean(ex.Message, "Neo4j could not be queried."), ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Knowledge graph read failed ({ExceptionType})")]
    private static partial void LogReadFailed(ILogger logger, string exceptionType);

    private static async Task EnsureConstraintsAsync(Neo4jService neo4j, string database, CancellationToken cancellationToken)
    {
        foreach (var (name, label) in new[] { ("table_key_unique", "Table"), ("column_key_unique", "Column"), ("database_key_unique", "Database") })
        {
            var created = await neo4j.CreateConstraintAsync(
                new Neo4jConstraintRequestModel
                {
                    Name = name,
                    Label = label,
                    Properties = ["key"],
                    ConstraintType = Neo4jConstraintType.Unique,
                    IfNotExists = true,
                    Database = database,
                },
                cancellationToken);
            if (!created.IsSuccess)
            {
                throw new KnowledgeGraphException(Clean(created.Message, "Neo4j rejected the schema setup."));
            }
        }

        // Full-text search over names and descriptions, so questions in plain English can later be matched to tables and columns.
        var text = await neo4j.CreateFullTextIndexAsync(
            new Neo4jFullTextIndexRequestModel
            {
                Name = "knowledge_search",
                Labels = ["Database", "Table", "Column"],
                Properties = ["name", "description"],
                IfNotExists = true,
                Database = database,
            },
            cancellationToken);
        if (!text.IsSuccess)
        {
            throw new KnowledgeGraphException(Clean(text.Message, "Neo4j rejected the search index setup."));
        }
    }


    private static async Task<Neo4j.Driver.IResultSummary> RunAsync(Neo4j.Driver.IAsyncQueryRunner runner, string cypher, Dictionary<string, object?> parameters)
    {
        var cursor = await runner.RunAsync(cypher, parameters!);
        return await cursor.ConsumeAsync();
    }

    private static Neo4jService Open(KnowledgeConnectionSettings s)
    {
        var model = new Neo4jConnectionModel { UseAura = s.UseAura };
        if (s.UseAura)
        {
            // The helper always turns encryption on for Aura in the driver config.
            model.Aura = new Neo4jAuraConnectionSettings { Uri = EncryptionByConfiguration(s.Uri), User = s.User, Password = s.Password, Database = s.Database };
        }
        else
        {
            model.Local = new Neo4jLocalConnectionSettings
            {
                Uri = s.Encrypted ? EncryptionByConfiguration(s.Uri) : s.Uri,
                User = s.User,
                Password = s.Password,
                Database = s.Database,
                Encrypted = s.Encrypted,
            };
        }

        return new Neo4jService(model);
    }

    /// <summary>
    /// The Neo4j driver rejects a URI scheme that already says "encrypted" (<c>neo4j+s</c>, <c>bolt+s</c>, <c>+ssc</c>) when encryption is
    /// also switched on in its configuration. The helper sets it in the configuration, so the scheme must be the plain one;
    /// the connection is still encrypted and the server certificate is still verified.
    /// </summary>
    internal static string EncryptionByConfiguration(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return uri;
        }

        var scheme = parsed.Scheme;
        var plain = scheme.EndsWith("+ssc", StringComparison.OrdinalIgnoreCase) ? scheme[..^4]
            : scheme.EndsWith("+s", StringComparison.OrdinalIgnoreCase) ? scheme[..^2]
            : scheme;
        return plain.Length == scheme.Length ? uri : new UriBuilder(parsed) { Scheme = plain }.Uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped).TrimEnd('/');
    }


    private static string DatabaseKey(string tenant, Guid? connectionId, string name) => $"{tenant}:db:{connectionId:N}:{name.ToUpperInvariant()}";

    private static string TableKey(string tenant, Guid versionId) => $"{tenant}:{versionId:N}";

    private static string ColumnKey(string tableKey, string column) => $"{tableKey}:{column.ToUpperInvariant()}";

    private static int Count(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null ? (int)Math.Min(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture), int.MaxValue) : 0;

    private static string Clean(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message.Length > 400 ? message[..400] : message;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Neo4j connection test failed ({ExceptionType})")]
    private static partial void LogTestFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Knowledge graph written: {Tables} tables, {Columns} columns, {Relationships} relationships")]
    private static partial void LogApplied(ILogger logger, int tables, int columns, int relationships);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Knowledge graph update failed ({ExceptionType})")]
    private static partial void LogApplyFailed(ILogger logger, string exceptionType);
}
