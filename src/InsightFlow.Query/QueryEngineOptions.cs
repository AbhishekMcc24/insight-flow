using System.ComponentModel.DataAnnotations;

namespace InsightFlow.Query;

/// <summary>Configuration of the query engine (section <c>QueryEngine</c>). Validated at startup.</summary>
public sealed class QueryEngineOptions
{
    public const string SectionName = "QueryEngine";

    /// <summary>Blob container that holds Parquet extracts.</summary>
    [Required]
    public string ExtractsContainer { get; set; } = "extracts";

    /// <summary>Local directory of the extract cache (the only directory DuckDB may read).</summary>
    [Required]
    public string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "insightflow", "extract-cache");

    /// <summary>Upper bound of the local extract cache, in bytes (default 10 GiB).</summary>
    [Range(1L << 20, long.MaxValue)]
    public long MaxCacheBytes { get; set; } = 10L << 30;

    /// <summary>Per-query timeout.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>DuckDB <c>memory_limit</c> per query connection (e.g. <c>2GB</c>).</summary>
    [Required]
    [RegularExpression(@"^\d+(\.\d+)?\s?(KB|MB|GB|TB|KiB|MiB|GiB|TiB)$")]
    public string MemoryLimit { get; set; } = "2GB";

    /// <summary>DuckDB worker threads per query (0 = DuckDB default: all cores).</summary>
    [Range(0, 256)]
    public int Threads { get; set; }

    /// <summary>How long chart results stay in Redis. Dataset versions are immutable, so this mainly bounds memory.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00")]
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(1);
}
