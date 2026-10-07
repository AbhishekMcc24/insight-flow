using System.Text.Json.Serialization;
using InsightFlow.Domain.Viz;

namespace InsightFlow.Contracts.Query;

/// <summary>Logical type of a result column, as seen by clients (renderers format values by it).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ColumnType>))]
public enum ColumnType
{
    String,
    Integer,
    Number,
    Boolean,
    Date,
    DateTime,
    Other,
}

/// <summary>
/// One column of a <see cref="QueryResult"/>. For chart queries <see cref="Channel"/> names the encoding channel
/// (<c>x</c>, <c>y</c>, <c>color</c>…) and <see cref="Field"/> the field reference that produced it, so a renderer can
/// map columns to visual channels without parsing SQL.
/// </summary>
public sealed record QueryColumn(string Name, ColumnType Type, string? Channel = null, FieldRef? Field = null);

/// <summary>
/// Tabular result returned by the query engine. Values are JSON primitives (string, number, boolean, null);
/// dates and timestamps are ISO-8601 strings. <see cref="Truncated"/> is true when more rows existed than the limit.
/// </summary>
public sealed record QueryResult(
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated);

/// <summary>Body of <c>POST /api/v1/query/viz</c>. Without a model id, the model containing the dataset version is used (or an implicit one).</summary>
public sealed record VizQueryRequest(VizSpec Spec, Guid? SemanticModelId = null);

/// <summary>Response of <c>POST /api/v1/query/viz</c>: rows plus the SQL that produced them (always shown to users).</summary>
public sealed record VizQueryResponse(QueryResult Result, string Sql, bool FromCache, double DurationMs);

/// <summary>Body of <c>POST /api/v1/query/preview</c>.</summary>
public sealed record PreviewRequest(Guid DatasetVersionId, int Limit = PreviewRequest.DefaultLimit)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1_000;
}

/// <summary>First rows and schema of a dataset version.</summary>
public sealed record PreviewResponse(Guid DatasetVersionId, QueryResult Result, long TotalRowCount);
