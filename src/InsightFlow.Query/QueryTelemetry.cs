using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace InsightFlow.Query;

/// <summary>
/// Traces and metrics of the query engine (<c>InsightFlow.Query</c>, picked up by ServiceDefaults' wildcard), so a
/// trace shows compile → cache → execute. Tags carry ids, counts and outcomes only — never row data or literals.
/// </summary>
public static class QueryTelemetry
{
    public const string Name = "InsightFlow.Query";

    public static readonly ActivitySource ActivitySource = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>(
        "insightflow.query.duration", unit: "ms", description: "End-to-end chart query time, tagged by cache outcome.");

    public static readonly Counter<long> CacheHits = Meter.CreateCounter<long>(
        "insightflow.query.cache.hits", description: "Chart queries answered from the result cache.");

    public static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>(
        "insightflow.query.cache.misses", description: "Chart queries executed against DuckDB.");

    public static readonly Counter<long> ExtractDownloads = Meter.CreateCounter<long>(
        "insightflow.query.extract.downloads", description: "Parquet extracts downloaded into the local cache.");
}
