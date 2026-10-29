using InsightFlow.Domain.Connections;

namespace InsightFlow.Infrastructure.Connectors;

/// <summary>A column as the source database declares it. <see cref="DataType"/> is the database's own type name (e.g. <c>nvarchar(100)</c>).</summary>
public sealed record SourceColumn(string Name, string DataType, bool IsNullable, bool IsPrimaryKey);

/// <summary>A table with its columns. <see cref="Table"/> carries the id discovery returns (e.g. <c>dbo.Orders</c>).</summary>
public sealed record SourceTableDefinition(SourceTable Table, IReadOnlyList<SourceColumn> Columns);

/// <summary>A foreign key the database enforces: <c>FromTable.FromColumn</c> references <c>ToTable.ToColumn</c> (table ids as in discovery).</summary>
public sealed record SourceForeignKey(string FromTable, string FromColumn, string ToTable, string ToColumn);

/// <summary>Tables, columns, primary keys and foreign keys read from the source's catalog (no row data).</summary>
public sealed record SourceSchema(IReadOnlyList<SourceTableDefinition> Tables, IReadOnlyList<SourceForeignKey> ForeignKeys);

/// <summary>
/// Optional connector capability: read table and column definitions (including primary and foreign keys) from a database's
/// catalog, for the knowledge base. Only metadata is read, never table rows. Connectors that cannot do this simply do not
/// implement it.
/// </summary>
public interface ISchemaDescriber
{
    /// <param name="profile">The saved connection.</param>
    /// <param name="tableIds">Discovery ids to describe; null or empty means every table and view.</param>
    /// <param name="cancellationToken">Cancels the catalog queries.</param>
    Task<SourceSchema> DescribeAsync(ConnectionProfile profile, IReadOnlyCollection<string>? tableIds, CancellationToken cancellationToken);
}

/// <summary>Shared helpers for catalog readers: selecting the requested tables and assembling the result.</summary>
internal static class SchemaAssembly
{
    public static SourceSchema Assemble(
        IEnumerable<(string Schema, string Name, string Kind, SourceColumn Column)> columns,
        IEnumerable<(string Schema, string Table, string Column)> primaryKeys,
        IEnumerable<SourceForeignKey> foreignKeys,
        IReadOnlyCollection<string>? tableIds)
    {
        var wanted = tableIds is { Count: > 0 } ? new HashSet<string>(tableIds, StringComparer.OrdinalIgnoreCase) : null;
        var keys = primaryKeys.Select(k => $"{k.Schema}.{k.Table}.{k.Column}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tables = columns
            .GroupBy(c => (c.Schema, c.Name, c.Kind))
            .Where(g => wanted is null || wanted.Contains($"{g.Key.Schema}.{g.Key.Name}"))
            .Select(g => new SourceTableDefinition(
                new SourceTable($"{g.Key.Schema}.{g.Key.Name}", g.Key.Name, g.Key.Schema, g.Key.Kind),
                g.Select(c => c.Column with { IsPrimaryKey = c.Column.IsPrimaryKey || keys.Contains($"{g.Key.Schema}.{g.Key.Name}.{c.Column.Name}") }).ToList()))
            .OrderBy(t => t.Table.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var present = tables.Select(t => t.Table.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Keep every foreign key that leaves a described table, even when its target is not described, so a preview can show where a column points.
        var relevant = foreignKeys.Where(f => present.Contains(f.FromTable)).Distinct().ToList();
        return new SourceSchema(tables, relevant);
    }

    public static string TypeWithLength(string dataType, object? length)
    {
        var text = Convert.ToString(length, System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(text) || text == "-1" ? dataType : $"{dataType}({text})";
    }
}

/// <summary>
/// Optional connector capability: list the databases a login can see on a server, so a person can pick one before browsing its
/// tables. Connectors for engines where a connection is bound to one database list the server's databases through the catalog.
/// </summary>
public interface IDatabaseCatalog
{
    Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken cancellationToken);
}

/// <summary>A stored procedure: where it lives and its source text (what the database keeps; never data).</summary>
public sealed record SourceProcedure(string? Schema, string Name, string Definition);

/// <summary>
/// Optional connector capability: read the stored procedures of the database a connection points at, so the AI can learn from the
/// queries people already run how the tables are used. Procedures whose source is hidden (encrypted) or that the login may not
/// see are left out.
/// </summary>
public interface IProcedureCatalog
{
    Task<IReadOnlyList<SourceProcedure>> ListProceduresAsync(ConnectionProfile profile, CancellationToken cancellationToken);
}
