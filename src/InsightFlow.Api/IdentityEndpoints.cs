using InsightFlow.Contracts.Identity;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace InsightFlow.Api;

/// <summary>Who-am-I endpoint: lets clients (and tests) see the tenant, user and roles the services resolved.</summary>
internal static class IdentityEndpoints
{
    public static RouteGroupBuilder MapIdentityEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/me", GetCurrentUser)
            .WithName("GetCurrentUser")
            .WithTags("Identity")
            .RequireAuthorization(InsightFlowPolicies.CanView);

        return group;
    }

    private static Ok<CurrentUserResponse> GetCurrentUser(ITenantContext tenant) =>
        TypedResults.Ok(new CurrentUserResponse(
            tenant.TenantId!.Value,
            tenant.UserId ?? string.Empty,
            tenant.Roles.Order(StringComparer.Ordinal).ToList()));
}
