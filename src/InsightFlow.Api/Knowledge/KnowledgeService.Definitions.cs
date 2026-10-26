using InsightFlow.Connectors;
using InsightFlow.Domain.Connections;
using InsightFlow.Contracts.Knowledge;
using InsightFlow.Domain.Knowledge;
using InsightFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Api.Knowledge;

/// <summary>
/// The editable part of the knowledge base: tables, columns and relationships stored per tenant. They come from a connected
/// database's catalog, from datasets, from people typing them in, or from AI suggestions a person accepted, and they are only
/// written to Neo4j when someone pushes the graph (<see cref="KnowledgeService.PushAsync"/>).
/// </summary>
public sealed partial class KnowledgeService
{
    public async Task<KnowledgeDefinitionsDto> GetDefinitionsAsync(CancellationToken cancellationToken)
    {
        var definitions = await db.LoadDefinitionsAsync(cancellationToken);
        var connectionNames = await db.ConnectionProfiles.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        return ToDto(definitions, connectionNames);
    }

    /// <summary>Reads the catalog of a saved connection (columns, keys and enforced foreign keys; never rows) and stores it as knowledge tables.</summary>
    public async Task<ImportResultDto> ImportFromConnectionAsync(ImportTablesRequest request, CancellationToken cancellationToken)
    {
        if (request.TableIds is null || request.TableIds.Count == 0)
        {
            throw new ApiProblemException("Choose at least one table to import.");
        }

        var profile = await db.ConnectionProfiles.SingleOrDefaultAsync(c => c.Id == request.ConnectionId, cancellationToken)
            ?? throw ApiProblemException.NotFound("The connection");
        if (connectors.Resolve(profile.Kind) is not ISchemaDescriber describer)
        {
            throw new ApiProblemException(
                StatusCodes.Status422UnprocessableEntity, "not_supported",
                $"Reading table definitions from {profile.Kind} is not supported yet. Add the tables by hand instead.");
        }

        var database = string.IsNullOrWhiteSpace(request.Database) ? profile.GetSetting("database") : request.Database.Trim();
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new ApiProblemException("Choose the database to import from.");
        }

        var scoped = ConnectionProfile.Create(
            profile.TenantId, profile.Name, profile.Kind, new Dictionary<string, string>(profile.Settings) { ["database"] = database }, profile.Secret, caller.UserId ?? "system", clock.GetUtcNow());
        var schema = await describer.DescribeAsync(scoped, request.TableIds, cancellationToken);
        var existing = await db.KnowledgeTables.Where(t => t.ConnectionId == profile.Id && t.DatabaseName == database).ToListAsync(cancellationToken);
        var existingByKey = existing.ToDictionary(t => TableKey(t.SchemaName, t.Name), StringComparer.OrdinalIgnoreCase);
        var total = await db.KnowledgeTables.CountAsync(cancellationToken);
        var now = clock.GetUtcNow();

        var created = new Dictionary<string, (KnowledgeTable Table, Dictionary<string, KnowledgeColumn> Columns)>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var columnCount = 0;
        foreach (var source in schema.Tables)
        {
            var key = TableKey(source.Table.Schema, source.Table.Name);
            if (existingByKey.ContainsKey(key))
            {
                skipped++;
                continue;
            }

            if (total + created.Count >= KnowledgeGraphPlanRules.MaxTables)
            {
                throw new ApiProblemException(StatusCodes.Status409Conflict, "too_many_tables", $"The knowledge base can hold at most {KnowledgeGraphPlanRules.MaxTables} tables.");
            }

            var table = KnowledgeTable.Create(Tenant, profile.Id, source.Table.Schema, source.Table.Name, null, DefinitionSource.Database, now, database);
            var columns = new Dictionary<string, KnowledgeColumn>(StringComparer.OrdinalIgnoreCase);
            var ordinal = 0;
            foreach (var column in source.Columns.Take(KnowledgeGraphPlanRules.MaxColumnsPerTable))
            {
                columns[column.Name] = KnowledgeColumn.Create(table, column.Name, column.DataType, null, column.IsPrimaryKey, column.IsNullable, ordinal++);
            }

            db.KnowledgeTables.Add(table);
            db.KnowledgeColumns.AddRange(columns.Values);
            created[key] = (table, columns);
            columnCount += columns.Count;
        }

        // Enforced foreign keys become relationships, when both ends are in the knowledge base (new or from an earlier import).
        var knownColumns = await LoadColumnLookupAsync(existing, cancellationToken);
        foreach (var (key, value) in created)
        {
            knownColumns[key] = value.Columns;
        }

        var relationshipCount = 0;
        var seen = (await db.KnowledgeRelationships.Select(r => new { r.FromColumnId, r.ToColumnId }).ToListAsync(cancellationToken))
            .Select(r => (r.FromColumnId, r.ToColumnId)).ToHashSet();
        foreach (var fk in schema.ForeignKeys)
        {
            if (knownColumns.TryGetValue(fk.FromTable, out var fromColumns) && fromColumns.TryGetValue(fk.FromColumn, out var from)
                && knownColumns.TryGetValue(fk.ToTable, out var toColumns) && toColumns.TryGetValue(fk.ToColumn, out var to)
                && from.Id != to.Id && seen.Add((from.Id, to.Id)))
            {
                db.KnowledgeRelationships.Add(KnowledgeRelationship.Create(
                    Tenant, from.Id, to.Id, RelationshipKind.ForeignKey, 1, "Foreign key declared in the database", DefinitionSource.Database));
                relationshipCount++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        LogImported(logger, created.Count, columnCount, relationshipCount, skipped);
        return new ImportResultDto(created.Count, columnCount, relationshipCount, skipped);
    }

    /// <summary>Adds the Shared workspace's datasets as knowledge tables (their column names and types, never rows).</summary>
    public async Task<ImportResultDto> ImportDatasetsAsync(CancellationToken cancellationToken)
    {
        var baseline = await db.LoadDatasetBaselineAsync(cancellationToken);
        var existing = await db.KnowledgeTables.Where(t => t.ConnectionId == null && t.Source == DefinitionSource.Dataset).ToListAsync(cancellationToken);
        var existingNames = existing.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var total = await db.KnowledgeTables.CountAsync(cancellationToken);
        var now = clock.GetUtcNow();

        var tables = 0;
        var columns = 0;
        var skipped = 0;
        foreach (var source in baseline.Tables)
        {
            if (!existingNames.Add(source.Name))
            {
                skipped++;
                continue;
            }

            if (total + tables >= KnowledgeGraphPlanRules.MaxTables)
            {
                throw new ApiProblemException(StatusCodes.Status409Conflict, "too_many_tables", $"The knowledge base can hold at most {KnowledgeGraphPlanRules.MaxTables} tables.");
            }

            var table = KnowledgeTable.Create(Tenant, null, null, source.Name, null, DefinitionSource.Dataset, now);
            db.KnowledgeTables.Add(table);
            var ordinal = 0;
            foreach (var column in source.Columns)
            {
                db.KnowledgeColumns.Add(KnowledgeColumn.Create(table, column.Name, column.DataType, null, false, true, ordinal++));
                columns++;
            }

            tables++;
        }

        await db.SaveChangesAsync(cancellationToken);
        LogImported(logger, tables, columns, 0, skipped);
        return new ImportResultDto(tables, columns, 0, skipped);
    }

    public async Task<KnowledgeTableDto> CreateTableAsync(SaveKnowledgeTableRequest request, CancellationToken cancellationToken)
    {
        if (await db.KnowledgeTables.CountAsync(cancellationToken) >= KnowledgeGraphPlanRules.MaxTables)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "too_many_tables", $"The knowledge base can hold at most {KnowledgeGraphPlanRules.MaxTables} tables.");
        }

        var table = KnowledgeTable.Create(Tenant, null, request.SchemaName, request.Name, request.Description, DefinitionSource.Manual, clock.GetUtcNow());
        db.KnowledgeTables.Add(table);
        await ApplyColumnsAsync(table, request.Columns, cancellationToken);
        await SaveUniqueAsync(cancellationToken);
        return await LoadTableDtoAsync(table.Id, cancellationToken);
    }

    public async Task<KnowledgeTableDto> UpdateTableAsync(Guid tableId, SaveKnowledgeTableRequest request, CancellationToken cancellationToken)
    {
        var table = await db.KnowledgeTables.SingleOrDefaultAsync(t => t.Id == tableId, cancellationToken) ?? throw ApiProblemException.NotFound("The table");
        if (table.IsStructureLocked)
        {
            await ApplyDescriptionsOnlyAsync(table, request, cancellationToken);
            return await LoadTableDtoAsync(table.Id, cancellationToken);
        }

        table.Update(request.SchemaName, request.Name, request.Description, clock.GetUtcNow());
        await ApplyColumnsAsync(table, request.Columns, cancellationToken);
        await SaveUniqueAsync(cancellationToken);
        return await LoadTableDtoAsync(table.Id, cancellationToken);
    }

    /// <summary>
    /// Removes one column from the knowledge base (and any relationships that use it). Only the knowledge base changes: the source
    /// database or dataset the table came from is never touched.
    /// </summary>
    public async Task RemoveColumnAsync(Guid columnId, CancellationToken cancellationToken)
    {
        var column = await db.KnowledgeColumns.SingleOrDefaultAsync(c => c.Id == columnId, cancellationToken) ?? throw ApiProblemException.NotFound("The column");

        // A composite relationship only means something with all its pairs, so losing one column removes the whole relationship.
        var touching = await db.KnowledgeRelationships.Where(r => r.FromColumnId == columnId || r.ToColumnId == columnId).ToListAsync(cancellationToken);
        db.KnowledgeRelationships.RemoveRange(await WithGroupMatesAsync(touching, cancellationToken));
        db.KnowledgeColumns.Remove(column);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A table that mirrors a database or dataset keeps its structure: names, types and keys cannot change and columns cannot be added
    /// (the source is never modified). Only descriptions are accepted; anything else in the request is refused rather than ignored.
    /// </summary>
    private async Task ApplyDescriptionsOnlyAsync(KnowledgeTable table, SaveKnowledgeTableRequest request, CancellationToken cancellationToken)
    {
        const string locked = "This table mirrors its source, so its structure can't be changed here. You can edit descriptions and remove columns from the knowledge base.";
        if (!string.Equals(request.Name?.Trim(), table.Name, StringComparison.Ordinal)
            || !string.Equals(string.IsNullOrWhiteSpace(request.SchemaName) ? null : request.SchemaName.Trim(), table.SchemaName, StringComparison.Ordinal))
        {
            throw ApiProblemException.Conflict(locked);
        }

        var stored = await db.KnowledgeColumns.Where(c => c.TableId == table.Id).ToDictionaryAsync(c => c.Id, cancellationToken);
        foreach (var item in request.Columns ?? [])
        {
            if (item.Id is not { } id || !stored.TryGetValue(id, out var column)
                || !string.Equals(item.Name?.Trim(), column.Name, StringComparison.Ordinal)
                || !string.Equals(item.DataType?.Trim(), column.DataType, StringComparison.Ordinal)
                || item.IsPrimaryKey != column.IsPrimaryKey
                || item.IsNullable != column.IsNullable)
            {
                throw ApiProblemException.Conflict(locked);
            }

            column.SetDescription(item.Description);
        }

        table.SetDescription(request.Description, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Removes tables from the knowledge base together with their columns and every relationship that touches those columns (including
    /// relationships to tables that stay). All or nothing. Only the knowledge base changes: the source database or dataset is never touched.
    /// </summary>
    public async Task<RemoveTablesResultDto> RemoveTablesAsync(IReadOnlyList<Guid> tableIds, CancellationToken cancellationToken)
    {
        var ids = (tableIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ApiProblemException("Choose at least one table to remove.");
        }

        var tables = await db.KnowledgeTables.Where(t => ids.Contains(t.Id)).ToListAsync(cancellationToken);
        if (tables.Count != ids.Count)
        {
            throw ApiProblemException.NotFound("One of the tables");
        }

        var columnIds = await db.KnowledgeColumns.Where(c => ids.Contains(c.TableId)).Select(c => c.Id).ToListAsync(cancellationToken);
        var relationships = await db.KnowledgeRelationships
            .Where(r => columnIds.Contains(r.FromColumnId) || columnIds.Contains(r.ToColumnId))
            .ToListAsync(cancellationToken);
        relationships = await WithGroupMatesAsync(relationships, cancellationToken);

        // The database cascades columns, but the relationships are removed explicitly so the count we report is exactly what was deleted.
        db.KnowledgeRelationships.RemoveRange(relationships);
        db.KnowledgeTables.RemoveRange(tables);
        await db.SaveChangesAsync(cancellationToken);
        LogTablesRemoved(logger, tables.Count, columnIds.Count, relationships.Count);
        return new RemoveTablesResultDto(tables.Count, columnIds.Count, relationships.Count);
    }

    public async Task DeleteTableAsync(Guid tableId, CancellationToken cancellationToken)
    {
        var table = await db.KnowledgeTables.SingleOrDefaultAsync(t => t.Id == tableId, cancellationToken) ?? throw ApiProblemException.NotFound("The table");
        db.KnowledgeTables.Remove(table); // columns and their relationships go with it (database cascade)
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<KnowledgeDefinitionsDto> AddRelationshipAsync(SaveKnowledgeRelationshipRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<RelationshipKind>(request.Kind, ignoreCase: true, out var kind))
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_relationship", "The relationship kind must be ForeignKey, SameAttribute or DerivedFrom.");
        }

        await AddRelationshipCoreAsync(request.FromColumnId, request.ToColumnId, kind, 1, request.Reason, DefinitionSource.Manual, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetDefinitionsAsync(cancellationToken);
    }

    public async Task DeleteRelationshipAsync(Guid relationshipId, CancellationToken cancellationToken)
    {
        var relationship = await db.KnowledgeRelationships.SingleOrDefaultAsync(r => r.Id == relationshipId, cancellationToken)
            ?? throw ApiProblemException.NotFound("The relationship");
        db.KnowledgeRelationships.RemoveRange(await WithGroupMatesAsync([relationship], cancellationToken)); // a composite goes as a whole
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Adds one composite relationship: several column pairs that must match together. The "from" columns must all be in one table and the
    /// "to" columns in one table, a pair cannot repeat, and none of the pairs may already be related.
    /// </summary>
    public async Task<KnowledgeDefinitionsDto> AddCompositeRelationshipAsync(SaveCompositeRelationshipRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<RelationshipKind>(request.Kind, ignoreCase: true, out var kind))
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_relationship", "The relationship kind must be ForeignKey, SameAttribute or DerivedFrom.");
        }

        var pairs = (request.Pairs ?? []).Distinct().ToList();
        if (pairs.Count is < 2 or > KnowledgeRelationship.MaxGroupSize)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_relationship", $"A composite relationship needs 2–{KnowledgeRelationship.MaxGroupSize} column pairs.");
        }

        var ids = pairs.SelectMany(p => new[] { p.FromColumnId, p.ToColumnId }).Distinct().ToList();
        var owner = await db.KnowledgeColumns.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.TableId, cancellationToken);
        if (owner.Count != ids.Count)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_relationship", "Every column must exist in the knowledge base.");
        }

        if (pairs.Select(p => owner[p.FromColumnId]).Distinct().Count() != 1 || pairs.Select(p => owner[p.ToColumnId]).Distinct().Count() != 1)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_relationship", "All the “from” columns must be in one table and all the “to” columns in one table.");
        }

        var groupId = Guid.CreateVersion7();
        foreach (var pair in pairs)
        {
            await AddRelationshipCoreAsync(pair.FromColumnId, pair.ToColumnId, kind, 1, request.Reason, DefinitionSource.Manual, cancellationToken, groupId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetDefinitionsAsync(cancellationToken);
    }

    /// <summary>The given relationships plus every other pair of any composite relationship they belong to (so a composite is never left half there).</summary>
    private async Task<List<KnowledgeRelationship>> WithGroupMatesAsync(List<KnowledgeRelationship> relationships, CancellationToken cancellationToken)
    {
        var groupIds = relationships.Where(r => r.GroupId is not null).Select(r => r.GroupId!.Value).Distinct().ToList();
        if (groupIds.Count == 0)
        {
            return relationships;
        }

        var mates = await db.KnowledgeRelationships.Where(r => r.GroupId != null && groupIds.Contains(r.GroupId.Value)).ToListAsync(cancellationToken);
        return [.. relationships.UnionBy(mates, r => r.Id)];
    }

    /// <summary>
    /// Writes (or clears) the description of a database that has tables in the knowledge base. Descriptions start at the database,
    /// then go to its tables and columns.
    /// </summary>
    public async Task<KnowledgeDefinitionsDto> SaveDatabaseDescriptionAsync(SaveDatabaseDescriptionRequest request, CancellationToken cancellationToken)
    {
        await UpsertDatabaseDescriptionAsync(request.ConnectionId, request.Name, request.Description, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetDefinitionsAsync(cancellationToken);
    }

    private async Task UpsertDatabaseDescriptionAsync(Guid? connectionId, string? name, string? description, CancellationToken cancellationToken)
    {
        var wanted = name?.Trim() ?? string.Empty;

        // Names are matched ignoring case, in memory (a connection has few databases), and stored as the tables spell them.
        var known = await db.KnowledgeTables.Where(t => t.ConnectionId == connectionId && t.DatabaseName != null).Select(t => t.DatabaseName!).Distinct().ToListAsync(cancellationToken);
        var canonical = known.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)) ?? throw ApiProblemException.NotFound("The database");

        var stored = (await db.KnowledgeDatabases.Where(d => d.ConnectionId == connectionId).ToListAsync(cancellationToken))
            .FirstOrDefault(d => string.Equals(d.Name, canonical, StringComparison.OrdinalIgnoreCase));
        if (stored is null)
        {
            db.KnowledgeDatabases.Add(KnowledgeDatabase.Create(Tenant, connectionId, canonical, description, clock.GetUtcNow()));
        }
        else
        {
            stored.SetDescription(description, clock.GetUtcNow());
        }
    }

    /// <summary>Stores the AI suggestions a person accepted. Ids are checked against the tenant's own tables, so a forged id changes nothing.</summary>
    public async Task<KnowledgeDefinitionsDto> ApplySuggestionsAsync(ApplySuggestionsRequest request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var tables = await db.KnowledgeTables.ToDictionaryAsync(t => t.Id, cancellationToken);
        var columns = await db.KnowledgeColumns.ToDictionaryAsync(c => c.Id, cancellationToken);

        foreach (var d in request.DatabaseDescriptions ?? [])
        {
            try
            {
                await UpsertDatabaseDescriptionAsync(d.ConnectionId, d.Database, d.Description, cancellationToken);
            }
            catch (ApiProblemException ex) when (ex.Status == StatusCodes.Status404NotFound)
            {
                // The database no longer has tables here: nothing to describe.
            }
        }

        foreach (var d in request.TableDescriptions ?? [])
        {
            if (tables.TryGetValue(d.TableId, out var table))
            {
                table.SetDescription(d.Description, now);
            }
        }

        foreach (var d in request.ColumnDescriptions ?? [])
        {
            if (columns.TryGetValue(d.ColumnId, out var column))
            {
                column.SetDescription(d.Description);
            }
        }

        // Removals first (and saved), so a person can swap one relationship for another in a single step.
        if (request.RemoveRelationshipIds is { Count: > 0 } removeIds)
        {
            var doomed = await db.KnowledgeRelationships.Where(r => removeIds.Contains(r.Id)).ToListAsync(cancellationToken);
            db.KnowledgeRelationships.RemoveRange(await WithGroupMatesAsync(doomed, cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var r in request.Relationships ?? [])
        {
            if (Enum.TryParse<RelationshipKind>(r.Kind, ignoreCase: true, out var kind))
            {
                try
                {
                    await AddRelationshipCoreAsync(r.FromColumnId, r.ToColumnId, kind, r.Confidence, r.Reason, DefinitionSource.Ai, cancellationToken);
                }
                catch (ApiProblemException ex) when (ex.Status is StatusCodes.Status409Conflict or StatusCodes.Status422UnprocessableEntity)
                {
                    // Already related, or the data types do not match: nothing to add.
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetDefinitionsAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private async Task AddRelationshipCoreAsync(
        Guid fromColumnId, Guid toColumnId, RelationshipKind kind, double confidence, string? reason, DefinitionSource source, CancellationToken cancellationToken, Guid? groupId = null)
    {
        var found = await db.KnowledgeColumns.Where(c => c.Id == fromColumnId || c.Id == toColumnId).Select(c => new { c.Id, c.Name, c.DataType }).ToListAsync(cancellationToken);
        if (found.Count != (fromColumnId == toColumnId ? 1 : 2))
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_relationship", "Both columns must exist in the knowledge base.");
        }

        var fromColumn = found.Single(c => c.Id == fromColumnId);
        var toColumn = found.Single(c => c.Id == toColumnId);
        if (!ColumnTypeCompatibility.AreCompatible(fromColumn.DataType, toColumn.DataType))
        {
            throw new ApiProblemException(
                StatusCodes.Status422UnprocessableEntity, "incompatible_types",
                $"This relationship is not possible: {fromColumn.Name} is {fromColumn.DataType} but {toColumn.Name} is {toColumn.DataType}. Columns can only be related when their data types match.");
        }

        if (await db.KnowledgeRelationships.AnyAsync(
                r => (r.FromColumnId == fromColumnId && r.ToColumnId == toColumnId) || (r.FromColumnId == toColumnId && r.ToColumnId == fromColumnId), cancellationToken)
            || db.KnowledgeRelationships.Local.Any(r => (r.FromColumnId == fromColumnId && r.ToColumnId == toColumnId) || (r.FromColumnId == toColumnId && r.ToColumnId == fromColumnId)))
        {
            throw ApiProblemException.Conflict("These columns are already related.");
        }

        db.KnowledgeRelationships.Add(KnowledgeRelationship.Create(Tenant, fromColumnId, toColumnId, kind, confidence, reason, source, groupId));
    }

    /// <summary>Makes the table's stored columns match the request: updates by id, adds new ones, removes the ones left out.</summary>
    private async Task ApplyColumnsAsync(KnowledgeTable table, IReadOnlyList<SaveKnowledgeColumnRequest> requested, CancellationToken cancellationToken)
    {
        requested ??= [];
        if (requested.Count > KnowledgeGraphPlanRules.MaxColumnsPerTable)
        {
            throw new ApiProblemException($"A table can have at most {KnowledgeGraphPlanRules.MaxColumnsPerTable} columns.");
        }

        var duplicate = requested.GroupBy(c => c.Name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "duplicate_column", $"The column '{duplicate.Key}' appears more than once.");
        }

        var stored = await db.KnowledgeColumns.Where(c => c.TableId == table.Id).ToDictionaryAsync(c => c.Id, cancellationToken);
        var keep = new HashSet<Guid>();
        var ordinal = 0;
        foreach (var item in requested)
        {
            if (item.Id is { } id && stored.TryGetValue(id, out var column))
            {
                column.Update(item.Name, item.DataType, item.Description, item.IsPrimaryKey, item.IsNullable, ordinal++);
                keep.Add(id);
            }
            else
            {
                var created = KnowledgeColumn.Create(table, item.Name, item.DataType, item.Description, item.IsPrimaryKey, item.IsNullable, ordinal++);
                db.KnowledgeColumns.Add(created);
                keep.Add(created.Id);
            }
        }

        var removedColumns = stored.Values.Where(c => !keep.Contains(c.Id)).ToList();
        if (removedColumns.Count > 0)
        {
            var removedIds = removedColumns.Select(c => c.Id).ToList();
            var touching = await db.KnowledgeRelationships.Where(r => removedIds.Contains(r.FromColumnId) || removedIds.Contains(r.ToColumnId)).ToListAsync(cancellationToken);
            db.KnowledgeRelationships.RemoveRange(await WithGroupMatesAsync(touching, cancellationToken)); // a composite never stays half there
        }

        foreach (var removed in removedColumns)
        {
            db.KnowledgeColumns.Remove(removed);
        }
    }

    private async Task SaveUniqueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw ApiProblemException.Conflict("A table or column with that name already exists.");
        }
    }

    private async Task<Dictionary<string, Dictionary<string, KnowledgeColumn>>> LoadColumnLookupAsync(
        IReadOnlyList<KnowledgeTable> tables, CancellationToken cancellationToken)
    {
        var ids = tables.Select(t => t.Id).ToList();
        var columns = await db.KnowledgeColumns.Where(c => ids.Contains(c.TableId)).ToListAsync(cancellationToken);
        return tables.ToDictionary(
            t => TableKey(t.SchemaName, t.Name),
            t => columns.Where(c => c.TableId == t.Id).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<KnowledgeTableDto> LoadTableDtoAsync(Guid tableId, CancellationToken cancellationToken)
    {
        var definitions = await db.LoadDefinitionsAsync(cancellationToken);
        var names = await db.ConnectionProfiles.ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        return ToDto(definitions, names).Tables.Single(t => t.Id == tableId);
    }

    private static string TableKey(string? schema, string name) => string.IsNullOrEmpty(schema) ? name : $"{schema}.{name}";

    private static KnowledgeDefinitionsDto ToDto(KnowledgeDefinitions definitions, Dictionary<Guid, string> connectionNames)
    {
        var columnsByTable = definitions.Columns.GroupBy(c => c.TableId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ordinal).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList());
        var tables = definitions.Tables
            .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(t => new KnowledgeTableDto(
                t.Id,
                t.ConnectionId,
                t.ConnectionId is { } c && connectionNames.TryGetValue(c, out var n) ? n : null,
                t.DatabaseName,
                t.SchemaName,
                t.Name,
                t.DisplayName,
                t.Description,
                t.Source.ToString(),
                t.IsStructureLocked,
                columnsByTable.GetValueOrDefault(t.Id, []).Select(col => new KnowledgeColumnDto(col.Id, col.Name, col.DataType, col.Description, col.IsPrimaryKey, col.IsNullable)).ToList()))
            .ToList();

        var tableOfColumn = definitions.Columns.ToDictionary(c => c.Id, c => c);
        var tableById = definitions.Tables.ToDictionary(t => t.Id, t => t);
        var relationships = definitions.Relationships
            .Where(r => tableOfColumn.ContainsKey(r.FromColumnId) && tableOfColumn.ContainsKey(r.ToColumnId))
            .Select(r =>
            {
                var from = tableOfColumn[r.FromColumnId];
                var to = tableOfColumn[r.ToColumnId];
                return new KnowledgeRelationshipDto(
                    r.Id, r.FromColumnId, r.ToColumnId,
                    tableById[from.TableId].DisplayName, from.Name, tableById[to.TableId].DisplayName, to.Name,
                    r.Kind.ToString(), r.Confidence, r.Reason, r.Source.ToString(), r.GroupId);
            })
            .OrderBy(r => r.FromTable, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.FromColumn, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var described = definitions.Databases.ToDictionary(d => (d.ConnectionId, d.Name.ToUpperInvariant()), d => d.Description);
        var databases = definitions.Tables
            .Where(t => !string.IsNullOrEmpty(t.DatabaseName))
            .GroupBy(t => (t.ConnectionId, Name: t.DatabaseName!))
            .OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KnowledgeDatabaseDto(
                g.Key.ConnectionId,
                g.Key.ConnectionId is { } cid && connectionNames.TryGetValue(cid, out var cn) ? cn : null,
                g.Key.Name,
                described.GetValueOrDefault((g.Key.ConnectionId, g.Key.Name.ToUpperInvariant())),
                g.Count()))
            .ToList();

        return new KnowledgeDefinitionsDto(tables, relationships, databases);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Knowledge base: removed {Tables} tables, {Columns} columns and {Relationships} relationships")]
    private static partial void LogTablesRemoved(ILogger logger, int tables, int columns, int relationships);

    [LoggerMessage(Level = LogLevel.Information, Message = "Knowledge import: {Tables} tables, {Columns} columns, {Relationships} relationships, {Skipped} tables already present")]
    private static partial void LogImported(ILogger logger, int tables, int columns, int relationships, int skipped);
}
