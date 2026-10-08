using System.ComponentModel.DataAnnotations;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Agents.Sandbox;

/// <summary>
/// What to run: AI-written SQL over one or more dataset versions (exposed as tables <c>input</c>, <c>input_2</c>, …).
/// With <see cref="Materialize"/> the complete result is written to Parquet by trusted code (for derived datasets).
/// </summary>
public sealed record SandboxRequest(
    TenantId Tenant,
    IReadOnlyList<DatasetVersion> Inputs,
    string Sql,
    TimeSpan? Timeout = null,
    int? RowCap = null,
    int PreviewRows = 20,
    bool Materialize = false);

public enum SandboxStatus
{
    Succeeded,

    /// <summary>The guard rejected the SQL before it ran.</summary>
    Rejected,

    /// <summary>DuckDB reported an error (bad column, type mismatch…).</summary>
    Failed,

    TimedOut,

    /// <summary>The result exceeded the row cap (for materialized results this is an error, not a silent truncation).</summary>
    TooManyRows,
}

/// <summary>
/// Outcome of a sandbox run. <see cref="Error"/> is safe to show to the user and to feed back to the model for repair
/// (it can quote the tenant's own data, so it is never logged). <see cref="ParquetPath"/> is owned by the caller,
/// who must delete <see cref="WorkDirectory"/> when done.
/// </summary>
public sealed record SandboxResult(
    SandboxStatus Status,
    QueryResult? Preview,
    long RowCount,
    DatasetSchema? Schema,
    string? Error,
    string? ParquetPath,
    string? WorkDirectory,
    TimeSpan Duration,
    SqlRejection Rejection = SqlRejection.None)
{
    public bool Success => Status == SandboxStatus.Succeeded;
}

/// <summary>
/// Runs AI-written SQL safely (D7). Every implementation must: accept only a single SELECT (via <see cref="SqlGuard"/>),
/// load inputs before locking the engine, disable external access and lock configuration, and enforce timeout, row cap
/// and memory limit. Tests in <c>InsightFlow.Agents.Tests</c> prove each property, including that cancellation interrupts
/// a running query (docs/adr/0022-sandbox-process-model.md).
/// </summary>
public interface ISqlSandbox
{
    Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken cancellationToken);
}

/// <summary>Sandbox limits (section <c>Sandbox</c>). Validated at startup.</summary>
public sealed class SandboxOptions
{
    public const string SectionName = "Sandbox";

    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(15);

    [Range(1, 50_000)]
    public int DefaultRowCap { get; set; } = 50_000;

    /// <summary>Upper bound for materialized (derived dataset) results.</summary>
    [Range(1, 100_000_000)]
    public int MaxMaterializedRows { get; set; } = 10_000_000;

    [Required]
    [RegularExpression(@"^\d+(\.\d+)?\s?(KB|MB|GB|TB|KiB|MiB|GiB|TiB)$")]
    public string MemoryLimit { get; set; } = "1GB";

    [Required]
    public string WorkDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "insightflow", "sandbox");
}
