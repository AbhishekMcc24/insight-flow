using System.Text.Json.Serialization;

namespace InsightFlow.Domain.Viz;

/// <summary>
/// A filter on one field. Polymorphic in JSON via the <c>"type"</c> discriminator so the UI, the AI and saved
/// workbooks all produce the same shape. Filter values are always bound as SQL parameters by the compiler.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EqualsFilter), "equals")]
[JsonDerivedType(typeof(InFilter), "in")]
[JsonDerivedType(typeof(RangeFilter), "range")]
[JsonDerivedType(typeof(RelativeDateFilter), "relativeDate")]
[JsonDerivedType(typeof(TopNFilter), "topN")]
public abstract record FilterSpec([property: JsonPropertyOrder(-1)] string Field);

/// <summary><c>field = value</c> (or <c>&lt;&gt;</c> when <see cref="Exclude"/> is set). A null value means <c>IS NULL</c>.</summary>
public sealed record EqualsFilter(string Field, ScalarValue Value, bool Exclude = false) : FilterSpec(Field);

/// <summary><c>field IN (values)</c> (or <c>NOT IN</c> when <see cref="Exclude"/> is set).</summary>
public sealed record InFilter(string Field, IReadOnlyList<ScalarValue> Values, bool Exclude = false) : FilterSpec(Field)
{
    public bool Equals(InFilter? other) =>
        other is not null && Field == other.Field && Exclude == other.Exclude && Values.SequenceEqual(other.Values);

    public override int GetHashCode() => HashCode.Combine(Field, Exclude, Values.Count);
}

/// <summary>Numeric or date range. At least one bound is required; bounds are inclusive by default.</summary>
public sealed record RangeFilter(
    string Field,
    ScalarValue? Min = null,
    ScalarValue? Max = null,
    bool MinInclusive = true,
    bool MaxInclusive = true) : FilterSpec(Field);

/// <summary>Date filter relative to "now" (evaluated in UTC at query time), e.g. last 3 months.</summary>
public sealed record RelativeDateFilter(
    string Field,
    TimeUnit Unit,
    RelativeDateAnchor Anchor,
    int Count = 1) : FilterSpec(Field);

/// <summary>Keeps the top (or bottom) <see cref="N"/> values of <see cref="FilterSpec.Field"/> ranked by <see cref="By"/>.</summary>
public sealed record TopNFilter(
    string Field,
    int N,
    FieldRef By,
    SortDirection Direction = SortDirection.Desc) : FilterSpec(Field);
