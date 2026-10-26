using InsightFlow.Domain.Threads;

namespace InsightFlow.Domain.Knowledge;

/// <summary>A table to put in the knowledge graph. <see cref="Id"/> is the knowledge-table id (or the dataset version id for a dataset baseline).</summary>
/// <summary><see cref="DatabaseName"/> (with <see cref="ConnectionId"/>) says which database of its server the table belongs to, so the graph can place it under that database.</summary>
public sealed record GraphTable(
    Guid Id, string Name, long RowCount, IReadOnlyList<GraphColumn> Columns, string? Description = null, string? Source = null, Guid? ConnectionId = null, string? DatabaseName = null);

/// <summary>A database on a server, the top of the hierarchy: database, then its tables, then their columns.</summary>
public sealed record GraphDatabase(Guid? ConnectionId, string Name, string? Description);

/// <summary>A column of a <see cref="GraphTable"/>.</summary>
public sealed record GraphColumn(Guid Id, string Name, string DataType, string? Description = null, bool IsPrimaryKey = false);

/// <summary>Why two columns are related.</summary>
public enum RelationshipKind
{
    /// <summary>Probably a foreign key: values of the first column point at rows of the second.</summary>
    ForeignKey,

    /// <summary>The same real-world attribute in two tables (e.g. <c>region</c> in sales and in targets).</summary>
    SameAttribute,

    /// <summary>One column is computed from the other.</summary>
    DerivedFrom,
}

/// <summary>
/// A link between two columns (named by table display name and column name), with confidence (0–1) and reasoning.
/// <see cref="GroupId"/> is shared by the pairs of one composite relationship (<see cref="GroupSize"/> pairs that must all match together).
/// </summary>
public sealed record GraphRelationship(
    string FromTable, string FromColumn, string ToTable, string ToColumn, RelationshipKind Kind, double Confidence, string Reason,
    Guid? GroupId = null, int GroupSize = 1);

/// <summary>A lineage link: <see cref="Table"/> was derived from <see cref="ParentTable"/> (Data Thread DAG).</summary>
public sealed record GraphLineage(string Table, string ParentTable);

/// <summary>
/// The knowledge graph as it will be written: tables, their columns, lineage between tables, and relationships between
/// columns. Built on the server from the stored definitions (never accepted from a browser), and the AI's suggestions are
/// checked by <see cref="KnowledgeGraphPlanRules"/> before they can be stored.
/// </summary>
public sealed record KnowledgeGraphPlan(
    IReadOnlyList<GraphTable> Tables,
    IReadOnlyList<GraphLineage> Lineage,
    IReadOnlyList<GraphRelationship> Relationships,
    IReadOnlyList<GraphDatabase>? Databases = null);

/// <summary>Pure rules for building and validating a <see cref="KnowledgeGraphPlan"/>.</summary>
public static class KnowledgeGraphPlanRules
{
    public const int MaxTables = 200;
    public const int MaxColumnsPerTable = 500;
    public const int MaxRelationships = 2_000;
    public const int MaxReasonLength = 300;
    public const int MaxDescriptionLength = 1_000;

    /// <summary>
    /// The facts-only plan for datasets: one table per dataset version (named after its source item when known, else its id),
    /// its columns, and lineage from <see cref="DatasetVersion.ParentIds"/> for parents that are in the same set.
    /// </summary>
    public static KnowledgeGraphPlan Baseline(IEnumerable<DatasetVersion> versions, IReadOnlyDictionary<Guid, string> namesByVersionId)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(namesByVersionId);

        var named = new List<(DatasetVersion Version, string Name)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions.Take(MaxTables))
        {
            var baseName = namesByVersionId.TryGetValue(version.Id, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : $"dataset_{version.Id:N}"[..16];
            named.Add((version, UniqueName(baseName, used)));
        }

        var nameById = named.ToDictionary(x => x.Version.Id, x => x.Name);
        var tables = named
            .Select(x => new GraphTable(
                x.Version.Id,
                x.Name,
                x.Version.RowCount,
                x.Version.Schema.Columns.Take(MaxColumnsPerTable).Select(c => new GraphColumn(Guid.Empty, c.Name, c.DataType.ToString())).ToList(),
                Source: "Dataset"))
            .ToList();

        var lineage = named
            .SelectMany(x => x.Version.ParentIds.Where(nameById.ContainsKey).Select(p => new GraphLineage(x.Name, nameById[p])))
            .ToList();

        return new KnowledgeGraphPlan(tables, lineage, []);
    }

    /// <summary>
    /// The plan for everything stored in the knowledge base. Table display names are made unique so a relationship can name its
    /// two ends unambiguously.
    /// </summary>
    public static KnowledgeGraphPlan FromDefinitions(
        IReadOnlyList<KnowledgeTable> tables, IReadOnlyList<KnowledgeColumn> columns, IReadOnlyList<KnowledgeRelationship> relationships,
        IReadOnlyList<KnowledgeDatabase>? databases = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(relationships);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = tables.OrderBy(t => t.CreatedAt).ToDictionary(t => t.Id, t => UniqueName(t.DisplayName, used));
        var columnsByTable = columns.GroupBy(c => c.TableId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ordinal).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList());

        var graphTables = tables
            .Select(t => new GraphTable(
                t.Id,
                names[t.Id],
                0,
                columnsByTable.GetValueOrDefault(t.Id, []).Select(c => new GraphColumn(c.Id, c.Name, c.DataType, c.Description, c.IsPrimaryKey)).ToList(),
                t.Description,
                t.Source.ToString(),
                t.ConnectionId,
                t.DatabaseName))
            .ToList();

        var columnOwner = columns.ToDictionary(c => c.Id, c => c);
        var groupSizes = relationships.Where(r => r.GroupId is not null).GroupBy(r => r.GroupId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var graphRelationships = relationships
            .Where(r => columnOwner.ContainsKey(r.FromColumnId) && columnOwner.ContainsKey(r.ToColumnId)
                && names.ContainsKey(columnOwner[r.FromColumnId].TableId) && names.ContainsKey(columnOwner[r.ToColumnId].TableId))
            .Select(r =>
            {
                var from = columnOwner[r.FromColumnId];
                var to = columnOwner[r.ToColumnId];
                return new GraphRelationship(
                    names[from.TableId], from.Name, names[to.TableId], to.Name, r.Kind, r.Confidence, r.Reason,
                    r.GroupId, r.GroupId is { } g ? groupSizes[g] : 1);
            })
            .ToList();

        // Every database that has tables, with its description when one was written (databases with no tables are not in the graph).
        var described = (databases ?? []).ToDictionary(d => (d.ConnectionId, d.Name.ToUpperInvariant()), d => d.Description);
        var graphDatabases = tables
            .Where(t => !string.IsNullOrEmpty(t.DatabaseName))
            .GroupBy(t => (t.ConnectionId, Name: t.DatabaseName!))
            .OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GraphDatabase(g.Key.ConnectionId, g.Key.Name, described.GetValueOrDefault((g.Key.ConnectionId, g.Key.Name.ToUpperInvariant()))))
            .ToList();

        return new KnowledgeGraphPlan(graphTables, [], graphRelationships, graphDatabases);
    }

    /// <summary>
    /// Keeps only well-formed relationships: both ends exist in <paramref name="plan"/>, they are not the same column, the
    /// confidence is within 0–1, and duplicates (either direction) collapse to the most confident one. Never throws on bad
    /// model output; it drops the bad items and reports how many were dropped.
    /// </summary>
    public static (IReadOnlyList<GraphRelationship> Valid, int Dropped) FilterRelationships(
        KnowledgeGraphPlan plan, IEnumerable<GraphRelationship> proposed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(proposed);

        var columns = plan.Tables.ToDictionary(
            t => t.Name,
            t => t.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var best = new Dictionary<string, GraphRelationship>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var r in proposed)
        {
            total++;
            if (r is null
                || !columns.TryGetValue(r.FromTable ?? string.Empty, out var fromColumns) || !fromColumns.Contains(r.FromColumn ?? string.Empty)
                || !columns.TryGetValue(r.ToTable ?? string.Empty, out var toColumns) || !toColumns.Contains(r.ToColumn ?? string.Empty)
                || (string.Equals(r.FromTable, r.ToTable, StringComparison.OrdinalIgnoreCase) && string.Equals(r.FromColumn, r.ToColumn, StringComparison.OrdinalIgnoreCase))
                || !Enum.IsDefined(r.Kind)
                || double.IsNaN(r.Confidence))
            {
                continue;
            }

            var clean = r with
            {
                Confidence = Math.Clamp(r.Confidence, 0, 1),
                Reason = Truncate(r.Reason, MaxReasonLength),
            };

            var a = $"{clean.FromTable}\u001f{clean.FromColumn}";
            var b = $"{clean.ToTable}\u001f{clean.ToColumn}";
            var key = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? $"{a}\u001e{b}" : $"{b}\u001e{a}";
            if (!best.TryGetValue(key, out var existing) || clean.Confidence > existing.Confidence)
            {
                best[key] = clean;
            }
        }

        var valid = best.Values.OrderByDescending(r => r.Confidence).ThenBy(r => r.FromTable, StringComparer.OrdinalIgnoreCase).Take(MaxRelationships).ToList();
        return (valid, total - valid.Count);
    }

    private static string UniqueName(string baseName, HashSet<string> used)
    {
        var name = baseName;
        for (var i = 2; !used.Add(name); i++)
        {
            name = $"{baseName} ({i})";
        }

        return name;
    }

    private static string Truncate(string? value, int max)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length > max ? text[..max] : text;
    }
}
