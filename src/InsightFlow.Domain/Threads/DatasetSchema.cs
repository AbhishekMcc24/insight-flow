using InsightFlow.Domain.Modeling;

namespace InsightFlow.Domain.Threads;

/// <summary>One physical column of a dataset version (as stored in its Parquet file).</summary>
public sealed record SchemaColumn(string Name, DataType DataType, bool Nullable = true);

/// <summary>The physical schema of a dataset version. Stored as JSONB; used for previews, modeling and agent grounding.</summary>
public sealed record DatasetSchema(IReadOnlyList<SchemaColumn> Columns)
{
    public static DatasetSchema Empty { get; } = new([]);

    public SchemaColumn? Find(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool Equals(DatasetSchema? other) => other is not null && Columns.SequenceEqual(other.Columns);

    public override int GetHashCode() => Columns.Count;
}
