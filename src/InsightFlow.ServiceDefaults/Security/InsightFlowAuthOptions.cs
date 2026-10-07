using System.ComponentModel.DataAnnotations;

namespace InsightFlow.ServiceDefaults.Security;

/// <summary>How callers are authenticated.</summary>
public enum AuthenticationMode
{
    /// <summary>Microsoft Entra External ID (OIDC / JWT bearer). The only mode allowed outside Development.</summary>
    Entra,

    /// <summary>Local development: a fake signed-in user for the seeded Contoso Retail tenant. Refused outside Development.</summary>
    Development,
}

/// <summary>
/// Authentication and tenancy settings (section <c>Authentication</c>). Claim names are configurable because
/// Entra External ID tenants expose the customer's tenant id through a custom claim.
/// </summary>
public sealed class InsightFlowAuthOptions
{
    public const string SectionName = "Authentication";

    public AuthenticationMode Mode { get; set; } = AuthenticationMode.Entra;

    /// <summary>Claim that carries the Insight Flow tenant id (a GUID).</summary>
    [Required]
    public string TenantClaimType { get; set; } = "tenant_id";

    /// <summary>Claim that carries the stable user id.</summary>
    [Required]
    public string UserIdClaimType { get; set; } = "sub";

    /// <summary>Claim that carries role names (<c>Viewer</c>, <c>Explorer</c>, <c>Creator</c>, <c>TenantAdmin</c>).</summary>
    [Required]
    public string RoleClaimType { get; set; } = "roles";
}
