using System.Buffers.Text;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InsightFlow.ServiceDefaults.Security;

/// <summary>
/// Development-only identity carried in an <c>Authorization: Dev &lt;base64url-json&gt;</c> header, so local tools,
/// integration tests and the Web app can act as any tenant/user/role without an identity provider.
/// </summary>
public sealed record DevToken(
    [property: JsonPropertyName("sub")] string UserId,
    [property: JsonPropertyName("tenant")] Guid TenantId,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("name")] string? Name = null)
{
    public const string Scheme = "Dev";

    /// <summary>The default signed-in development user of the seeded Contoso Retail tenant.</summary>
    public static DevToken Default { get; } =
        new(DevelopmentIdentity.UserId, DevelopmentIdentity.TenantId, DevelopmentIdentity.Roles, DevelopmentIdentity.UserName);

    /// <summary>Value for the <c>Authorization</c> header.</summary>
    public string ToHeaderValue() =>
        $"{Scheme} {Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this, DevTokenJsonContext.Default.DevToken))}";

    public static bool TryParse(string? headerValue, out DevToken? token)
    {
        token = null;
        if (headerValue is null || !headerValue.StartsWith(Scheme + " ", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var json = Base64Url.DecodeFromChars(headerValue.AsSpan(Scheme.Length + 1));
            token = JsonSerializer.Deserialize(json, DevTokenJsonContext.Default.DevToken);
            return token is not null && !string.IsNullOrWhiteSpace(token.UserId);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    public bool Equals(DevToken? other) =>
        other is not null && UserId == other.UserId && TenantId == other.TenantId && Name == other.Name && Roles.SequenceEqual(other.Roles);

    public override int GetHashCode() => HashCode.Combine(UserId, TenantId, Name);
}

[JsonSerializable(typeof(DevToken))]
internal sealed partial class DevTokenJsonContext : JsonSerializerContext;

/// <summary>
/// Signs in a fake user in Development. With no <c>Authorization</c> header the default Contoso Retail user is used;
/// a <c>Dev</c> token impersonates another tenant/user/role (used by isolation tests). Registration refuses to run
/// outside the Development environment, and the handler double-checks at request time (defence in depth).
/// </summary>
internal sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHostEnvironment environment,
    IOptions<InsightFlowAuthOptions> authOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsDevelopment())
        {
            return Task.FromResult(AuthenticateResult.Fail("Development authentication is disabled outside Development."));
        }

        var header = Request.Headers.Authorization.ToString();
        DevToken? token;
        if (string.IsNullOrEmpty(header))
        {
            token = DevToken.Default;
        }
        else if (!DevToken.TryParse(header, out token))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid development token."));
        }

        var auth = authOptions.Value;
        List<Claim> claims =
        [
            new(auth.UserIdClaimType, token!.UserId),
            new(auth.TenantClaimType, token.TenantId.ToString("D")),
            new(ClaimTypes.Name, token.Name ?? token.UserId),
        ];
        claims.AddRange(token.Roles.Select(r => new Claim(auth.RoleClaimType, r)));

        var identity = new ClaimsIdentity(claims, DevToken.Scheme, ClaimTypes.Name, auth.RoleClaimType);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), DevToken.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
