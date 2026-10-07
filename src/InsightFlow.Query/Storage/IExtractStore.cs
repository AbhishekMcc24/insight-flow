using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Query.Storage;

/// <summary>
/// Where Parquet extracts live. The durable copy is in Blob Storage under
/// <c>tenants/{tenantId}/extracts/{datasetVersionId}.parquet</c>; queries read a local copy because DuckDB runs with
/// external access disabled (only <see cref="LocalRoot"/> is readable).
/// </summary>
public interface IExtractStore
{
    /// <summary>Directory that contains every local extract; DuckDB is allowed to read only below it.</summary>
    string LocalRoot { get; }

    /// <summary>Returns a local file path for the version's Parquet, downloading it into the bounded cache if needed.</summary>
    Task<string> GetLocalPathAsync(DatasetVersion version, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a new extract (immutable: an existing blob is never overwritten) and warms the local cache.
    /// Returns the blob URI.
    /// </summary>
    Task<Uri> SaveAsync(TenantId tenant, Guid datasetVersionId, Stream parquet, CancellationToken cancellationToken);
}
