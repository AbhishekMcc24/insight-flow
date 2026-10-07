namespace InsightFlow.Domain.Tenancy;

/// <summary>
/// Roles a user can hold inside a tenant. Ordered from least to most privileged so that
/// "at least Explorer" checks are a simple comparison. Claim values use the enum names.
/// </summary>
public enum TenantRole
{
    /// <summary>Reads dashboards and shared content.</summary>
    Viewer = 0,

    /// <summary>Viewer + ad-hoc exploration and AI questions over shared data.</summary>
    Explorer = 1,

    /// <summary>Explorer + creates datasets, models, workbooks and shared content.</summary>
    Creator = 2,

    /// <summary>Creator + tenant administration (users, roles, connections).</summary>
    TenantAdmin = 3,
}
