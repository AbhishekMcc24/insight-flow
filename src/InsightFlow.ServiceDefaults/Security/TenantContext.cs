using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace InsightFlow.ServiceDefaults.Security;

/// <summary>
/// The caller of the current request: tenant, user and roles, read from token claims. Every tenant-scoped query and
/// write is keyed off <see cref="TenantId"/>. Kept free of Domain types so every host (including Web) can use it;
/// Persistence adapts it to its strongly-typed <c>TenantId</c>.
/// </summary>
public interface ITenantContext
{
    /// <summary>The caller's tenant, or null for anonymous requests (only health endpoints allow those).</summary>
    Guid? TenantId { get; }

    string? UserId { get; }

    IReadOnlySet<string> Roles { get; }

    bool IsInRole(string role);
}

/// <summary><see cref="ITenantContext"/> backed by the current <see cref="HttpContext"/> user.</summary>
internal sealed class ClaimsTenantContext(IHttpContextAccessor accessor, IOptions<InsightFlowAuthOptions> options) : ITenantContext
{
    private readonly InsightFlowAuthOptions _options = options.Value;

    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public Guid? TenantId =>
        User is { } user && TryReadTenant(user, _options.TenantClaimType, out var tenant) ? tenant : null;

    public string? UserId => User?.FindFirst(_options.UserIdClaimType)?.Value;

    public IReadOnlySet<string> Roles =>
        User?.FindAll(_options.RoleClaimType).Select(c => c.Value).ToHashSet(StringComparer.Ordinal) ?? [];

    public bool IsInRole(string role) => Roles.Contains(role);

    internal static bool TryReadTenant(ClaimsPrincipal user, string claimType, [NotNullWhen(true)] out Guid? tenant)
    {
        tenant = null;
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var values = user.FindAll(claimType).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Exactly one tenant per token: ambiguity is treated as "no tenant" (fail closed).
        if (values.Count != 1 || !Guid.TryParse(values[0], out var guid) || guid == Guid.Empty)
        {
            return false;
        }

        tenant = guid;
        return true;
    }
}
