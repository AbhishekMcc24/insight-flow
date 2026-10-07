using System.ComponentModel.DataAnnotations;

namespace InsightFlow.Connectors;

/// <summary>Configuration of connectors and the extract pipeline (section <c>Connectors</c>). Validated at startup.</summary>
public sealed class ConnectorOptions
{
    public const string SectionName = "Connectors";

    /// <summary>Scratch directory for downloaded source files, DuckDB spill files and Parquet output (cleaned per run).</summary>
    [Required]
    public string WorkDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "insightflow", "extract-work");

    [Required]
    public string ExtractsContainer { get; set; } = "extracts";

    [Required]
    public string FilesContainer { get; set; } = "files";

    /// <summary>DuckDB memory limit for one extract (larger data spills to <see cref="WorkDirectory"/>).</summary>
    [Required]
    [RegularExpression(@"^\d+(\.\d+)?\s?(KB|MB|GB|TB|KiB|MiB|GiB|TiB)$")]
    public string MemoryLimit { get; set; } = "2GB";

    /// <summary>Command timeout for source databases.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "06:00:00")]
    public TimeSpan SourceCommandTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Documents sampled to infer a schema from document stores (MongoDB, Cosmos DB).</summary>
    [Range(1, 1_000_000)]
    public int DocumentSampleSize { get; set; } = 1_000;
}
