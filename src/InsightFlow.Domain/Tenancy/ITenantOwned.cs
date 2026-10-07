namespace InsightFlow.Domain.Tenancy;

/// <summary>
/// Marks an entity that belongs to exactly one tenant. Persistence applies a global query filter and a write guard
/// to every implementation, so forgetting a <c>WHERE tenant_id = …</c> cannot leak data between tenants.
/// </summary>
public interface ITenantOwned
{
    TenantId TenantId { get; }
}
