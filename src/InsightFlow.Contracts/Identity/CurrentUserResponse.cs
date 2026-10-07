namespace InsightFlow.Contracts.Identity;

/// <summary>The caller as the services see them (tenant, user, roles). Returned by <c>GET /api/v1/me</c>.</summary>
public sealed record CurrentUserResponse(Guid TenantId, string UserId, IReadOnlyList<string> Roles);
