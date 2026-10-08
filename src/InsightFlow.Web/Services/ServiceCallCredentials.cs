using System.Net.Http.Headers;
using System.Security.Claims;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace InsightFlow.Web.Services;

/// <summary>
/// Puts the signed-in user's identity on outgoing service calls, so Api/QueryService/AgentService authorize the
/// *user* (tenant + roles), never the Web app itself.
/// <list type="bullet">
/// <item>Development: a <see cref="DevToken"/> rebuilt from the user's claims (the services run the same dev handler).</item>
/// <item>Entra External ID: an access token for <c>DownstreamApi:Scopes</c> acquired on behalf of the user.</item>
/// </list>
/// Scoped: inside a Blazor circuit the user comes from <see cref="AuthenticationStateProvider"/>; in plain HTTP
/// endpoints (BFF proxy) from the current <see cref="HttpContext"/>.
/// </summary>
public sealed class ServiceCallCredentials(
    IServiceProvider services,
    IHttpContextAccessor httpContextAccessor,
    IOptions<InsightFlowAuthOptions> authOptions,
    IConfiguration configuration)
{
    public async Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await GetUserAsync();
        if (user.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("No signed-in user for the service call.");
        }

        var auth = authOptions.Value;
        if (auth.Mode == AuthenticationMode.Development)
        {
            var tenant = Guid.Parse(user.FindFirstValue(auth.TenantClaimType) ?? throw new InvalidOperationException("Missing tenant claim."));
            var token = new DevToken(
                user.FindFirstValue(auth.UserIdClaimType) ?? throw new InvalidOperationException("Missing user claim."),
                tenant,
                user.FindAll(auth.RoleClaimType).Select(c => c.Value).ToList(),
                user.Identity.Name);
            request.Headers.TryAddWithoutValidation("Authorization", token.ToHeaderValue());
            return;
        }

        // TODO(dev2): verify token acquisition against a real Entra External ID tenant (see docs/handoff-dev2.md).
        var scopes = configuration.GetSection("DownstreamApi:Scopes").Get<string[]>() ?? [];
        var tokens = services.GetRequiredService<ITokenAcquisition>();
        var accessToken = await tokens.GetAccessTokenForUserAsync(scopes, user: user);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<ClaimsPrincipal> GetUserAsync()
    {
        // In a plain request (BFF endpoint, prerender) HttpContext is authoritative. Inside an interactive circuit there
        // is no request, and the circuit's authentication state is the source of truth.
        if (httpContextAccessor.HttpContext is { WebSockets.IsWebSocketRequest: false } http && http.User.Identity?.IsAuthenticated == true)
        {
            return http.User;
        }

        var state = await services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
        return state.User;
    }
}
