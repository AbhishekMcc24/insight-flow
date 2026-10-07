using System.Text.Json.Serialization;

namespace InsightFlow.Domain.Viz;

/// <summary>Visual mark of a chart (grammar of graphics).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Mark>))]
public enum Mark
{
    Bar,
    Line,
    Area,
    Point,
    Pie,
    Heatmap,
    Table,
}

/// <summary>Aggregation applied to a field. <see cref="None"/> means the raw (grouped) value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Agg>))]
public enum Agg
{
    None,
    Sum,
    Avg,
    Count,
    CountDistinct,
    Min,
    Max,
    Median,
}

/// <summary>Truncation unit for date/time fields.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TimeUnit>))]
public enum TimeUnit
{
    Year,
    Quarter,
    Month,
    Week,
    Day,
    Hour,
}

[JsonConverter(typeof(JsonStringEnumConverter<SortDirection>))]
public enum SortDirection
{
    Asc,
    Desc,
}

/// <summary>Anchor of a relative date filter, e.g. "last 3 months", "current quarter", "year to date".</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RelativeDateAnchor>))]
public enum RelativeDateAnchor
{
    /// <summary>The <c>Count</c> complete units before the current one.</summary>
    Last,

    /// <summary>The current (possibly partial) unit.</summary>
    Current,

    /// <summary>The <c>Count</c> units after the current one.</summary>
    Next,

    /// <summary>From the start of the current unit up to now.</summary>
    ToDate,
}
