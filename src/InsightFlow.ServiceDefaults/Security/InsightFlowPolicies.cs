using Microsoft.AspNetCore.Authorization;

namespace InsightFlow.ServiceDefaults.Security;

/// <summary>Role names as they appear in the role claim. Mirrors <c>InsightFlow.Domain.Tenancy.TenantRole</c>.</summary>
public static class InsightFlowRoles
{
    public const string Viewer = nameof(Viewer);
    public const string Explorer = nameof(Explorer);
    public const string Creator = nameof(Creator);
    public const string TenantAdmin = nameof(TenantAdmin);

    /// <summary>All roles, least to most privileged.</summary>
    public static readonly IReadOnlyList<string> All = [Viewer, Explorer, Creator, TenantAdmin];
}

/// <summary>
/// The single place where authorization policies are defined. Roles are hierarchical, so each policy accepts its
/// role and every role above it. Endpoints use <c>.RequireAuthorization(InsightFlowPolicies.CanCreate)</c>.
/// Role management screens are Developer 2's (TODO(dev2)); this is the plumbing they build on.
/// </summary>
public static class InsightFlowPolicies
{
    /// <summary>Default for every endpoint: an authenticated caller with a valid tenant claim (fallback policy).</summary>
    public const string TenantMember = nameof(TenantMember);

    public const string CanView = nameof(CanView);
    public const string CanExplore = nameof(CanExplore);
    public const string CanCreate = nameof(CanCreate);
    public const string CanAdministerTenant = nameof(CanAdministerTenant);

    internal static void Configure(AuthorizationOptions options, InsightFlowAuthOptions auth)
    {
        var tenantMember = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ClaimsTenantContext.TryReadTenant(ctx.User, auth.TenantClaimType, out _))
            .Build();

        options.AddPolicy(TenantMember, tenantMember);
        options.FallbackPolicy = tenantMember;

        options.AddPolicy(CanView, AtLeast(tenantMember, auth, InsightFlowRoles.Viewer));
        options.AddPolicy(CanExplore, AtLeast(tenantMember, auth, InsightFlowRoles.Explorer));
        options.AddPolicy(CanCreate, AtLeast(tenantMember, auth, InsightFlowRoles.Creator));
        options.AddPolicy(CanAdministerTenant, AtLeast(tenantMember, auth, InsightFlowRoles.TenantAdmin));
    }

    private static AuthorizationPolicy AtLeast(AuthorizationPolicy basePolicy, InsightFlowAuthOptions auth, string minimumRole)
    {
        var allowed = InsightFlowRoles.All.SkipWhile(r => r != minimumRole).ToHashSet(StringComparer.Ordinal);
        return new AuthorizationPolicyBuilder()
            .Combine(basePolicy)
            .RequireAssertion(ctx => ctx.User.FindAll(auth.RoleClaimType).Any(c => allowed.Contains(c.Value)))
            .Build();
    }
}
