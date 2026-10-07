using System.Text.Json.Serialization;

namespace InsightFlow.Domain.Viz;

/// <summary>
/// A reference to a semantic-model field, optionally aggregated and/or truncated to a time unit.
/// <see cref="Field"/> is <c>column</c>, <c>table.column</c> or a calculated measure name.
/// </summary>
public sealed record FieldRef(string Field, Agg Agg = Agg.None, TimeUnit? TimeUnit = null)
{
    [JsonIgnore]
    public bool IsAggregated => Agg != Agg.None;
}

/// <summary>Mapping from visual channels to fields.</summary>
public sealed record Encoding(
    FieldRef? X,
    FieldRef? Y,
    FieldRef? Color = null,
    FieldRef? Size = null,
    FieldRef? Facet = null,
    FieldRef? Label = null)
{
    /// <summary>All channels that are set, in channel order (X, Y, Color, Size, Facet, Label).</summary>
    public IEnumerable<FieldRef> All() =>
        new[] { X, Y, Color, Size, Facet, Label }.OfType<FieldRef>();
}

/// <summary>Sort on an encoded field (by its field name).</summary>
public sealed record SortSpec(string Field, SortDirection Direction);

/// <summary>
/// The canonical, versioned description of every chart (D5). The UI, the AI and saved workbooks all produce
/// and consume this one record; the query engine compiles it to SQL and the Web renders it with Vega-Lite.
/// Bump <see cref="CurrentSchemaVersion"/> on breaking changes and add an upgrader.
/// </summary>
public sealed record VizSpec(
    int SchemaVersion,
    Guid DatasetVersionId,
    Mark Mark,
    Encoding Encoding,
    IReadOnlyList<FilterSpec> Filters,
    IReadOnlyList<SortSpec>? Sort = null,
    int Limit = VizSpec.DefaultLimit)
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultLimit = 5_000;
    public const int MaxLimit = 50_000;

    public bool Equals(VizSpec? other) =>
        other is not null
        && SchemaVersion == other.SchemaVersion
        && DatasetVersionId == other.DatasetVersionId
        && Mark == other.Mark
        && Encoding == other.Encoding
        && Filters.SequenceEqual(other.Filters)
        && (Sort ?? []).SequenceEqual(other.Sort ?? [])
        && Limit == other.Limit;

    public override int GetHashCode() =>
        HashCode.Combine(SchemaVersion, DatasetVersionId, Mark, Encoding, Filters.Count, Limit);
}
