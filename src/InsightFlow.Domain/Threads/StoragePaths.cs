using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Threads;

/// <summary>
/// The single place that builds blob paths. Paths contain only ids (never user-supplied names), always start
/// with the tenant prefix (D1: per-tenant blob prefix) and are relative to their container, so the same value
/// works against Azurite locally and Azure Storage in the cloud.
/// </summary>
public static class StoragePaths
{
    /// <summary>Extract path inside the <c>extracts</c> container: <c>tenants/{tenantId}/extracts/{datasetVersionId}.parquet</c>.</summary>
    public static string Extract(TenantId tenant, Guid datasetVersionId) =>
        $"{TenantPrefix(tenant)}/extracts/{datasetVersionId:D}.parquet";

    /// <summary>Uploaded-file path inside the <c>files</c> container: <c>tenants/{tenantId}/files/{storedFileId}</c>.</summary>
    public static string File(TenantId tenant, Guid storedFileId) =>
        $"{TenantPrefix(tenant)}/files/{storedFileId:D}";

    public static string TenantPrefix(TenantId tenant)
    {
        if (tenant.IsEmpty)
        {
            throw new ArgumentException("A tenant is required to build a storage path.", nameof(tenant));
        }

        return $"tenants/{tenant.Value:D}";
    }
}
