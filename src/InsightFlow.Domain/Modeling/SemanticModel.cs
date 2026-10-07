using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Modeling;

/// <summary>Whether a column is used to group/slice (dimension) or to aggregate (measure) by default.</summary>
public enum ColumnRole
{
    Dimension,
    Measure,
}

/// <summary>How rows of two related tables match.</summary>
public enum Cardinality
{
    /// <summary>Many rows of the "from" table match one row of the "to" table (fact → dimension).</summary>
    ManyToOne,
    OneToOne,
}

/// <summary>
/// A column as the business sees it. Names are physical column names (quoted by the dialect, never
/// interpolated from user text); display name, description and synonyms ground the AI agents.
/// </summary>
public sealed record ModelColumn(
    string Name,
    DataType DataType,
    ColumnRole Role,
    string? DisplayName = null,
    string? Description = null,
    IReadOnlyList<string>? Synonyms = null,
    string? Format = null)
{
    public IReadOnlyList<string> Synonyms { get; init; } = Synonyms ?? [];
}

/// <summary>A logical table backed by one dataset version (Parquet extract).</summary>
public sealed record ModelTable(
    string Name,
    Guid SourceDatasetVersionId,
    IReadOnlyList<ModelColumn> Columns,
    string? DisplayName = null,
    string? Description = null)
{
    public ModelColumn? FindColumn(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One pair of join columns.</summary>
public sealed record JoinKey(string FromColumn, string ToColumn);

/// <summary>A join path between two tables; the compiler uses it to add joins when a spec spans tables.</summary>
public sealed record Relationship(string FromTable, string ToTable, IReadOnlyList<JoinKey> Keys, Cardinality Cardinality = Cardinality.ManyToOne);

/// <summary>
/// A named aggregate expression in DuckDB SQL (e.g. <c>SUM(revenue) - SUM(cost)</c>). Validated through the
/// AI-SQL sandbox before it is saved, so it is already aggregated and must not be wrapped in another aggregation.
/// </summary>
public sealed record CalculatedMeasure(
    string Name,
    string Expression,
    DataType ResultType,
    string? DisplayName = null,
    string? Description = null,
    string? Format = null);

/// <summary>
/// The governed business model over one or more dataset versions: tables, columns, relationships and
/// calculated measures. Modelled once, used by both the SQL compiler (joins, column resolution) and the
/// agents (prompt grounding).
/// </summary>
public sealed record SemanticModel(
    Guid Id,
    TenantId TenantId,
    string Name,
    IReadOnlyList<ModelTable> Tables,
    IReadOnlyList<Relationship>? Relationships = null,
    IReadOnlyList<CalculatedMeasure>? Measures = null)
{
    public IReadOnlyList<Relationship> Relationships { get; init; } = Relationships ?? [];

    public IReadOnlyList<CalculatedMeasure> Measures { get; init; } = Measures ?? [];

    public ModelTable? FindTable(string name) =>
        Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a field reference: <c>table.column</c>, a column name that is unique across tables, or a
    /// calculated measure name. Ambiguous or unknown names fail with a reason suitable for users and agents.
    /// </summary>
    public FieldResolution Resolve(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return FieldResolution.Failed("Field name is empty.");
        }

        var measure = Measures.FirstOrDefault(m => string.Equals(m.Name, field, StringComparison.OrdinalIgnoreCase));
        if (measure is not null)
        {
            return FieldResolution.ForMeasure(measure);
        }

        var dot = field.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && FindTable(field[..dot]) is { } qualifiedTable)
        {
            var column = qualifiedTable.FindColumn(field[(dot + 1)..]);
            return column is null
                ? FieldResolution.Failed($"Table '{qualifiedTable.Name}' has no column '{field[(dot + 1)..]}'.")
                : FieldResolution.ForColumn(qualifiedTable, column);
        }

        var matches = Tables
            .Select(t => (Table: t, Column: t.FindColumn(field)))
            .Where(m => m.Column is not null)
            .ToList();

        return matches.Count switch
        {
            1 => FieldResolution.ForColumn(matches[0].Table, matches[0].Column!),
            0 => FieldResolution.Failed($"Unknown field '{field}'."),
            _ => FieldResolution.Failed(
                $"Field '{field}' is ambiguous; qualify it as one of: {string.Join(", ", matches.Select(m => $"{m.Table.Name}.{m.Column!.Name}"))}."),
        };
    }
}

/// <summary>Result of <see cref="SemanticModel.Resolve"/>: either a table column or a calculated measure, or an error.</summary>
public sealed record FieldResolution(ModelTable? Table, ModelColumn? Column, CalculatedMeasure? Measure, string? Error)
{
    public bool Success => Error is null;

    public bool IsMeasure => Measure is not null;

    /// <summary>The logical type of the resolved field.</summary>
    public DataType DataType => Column?.DataType ?? Measure?.ResultType ?? DataType.String;

    public static FieldResolution ForColumn(ModelTable table, ModelColumn column) => new(table, column, null, null);

    public static FieldResolution ForMeasure(CalculatedMeasure measure) => new(null, null, measure, null);

    public static FieldResolution Failed(string error) => new(null, null, null, error);
}
