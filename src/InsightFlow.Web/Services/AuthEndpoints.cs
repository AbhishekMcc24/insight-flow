using System.Net.Mail;
using InsightFlow.Foundation.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace InsightFlow.Web.Services;

/// <summary>
/// Where the sign-in and registration screens hand over. Passwords never touch this app: in the cloud the person is sent to Microsoft Entra
/// External ID (which asks for the password, multi-factor and consent), locally the development handler has already signed the person in.
/// The screens only collect an email, passed on as a hint, so the identity provider can pre-fill it.
/// </summary>
public static class AuthEndpoints
{
    private const string OidcScheme = "OpenIdConnect";
    private const string CookieScheme = "Cookies";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").AllowAnonymous();

        // A form post (antiforgery is checked by the framework), so a third-party page cannot start a sign-in for someone else.
        group.MapPost("/signin", (IFormCollection form, IOptions<InsightFlowAuthOptions> auth) =>
        {
            var register = form["mode"] == "register";
            var returnUrl = AuthRedirect.Safe(form["returnUrl"]);
            if (register && form["terms"] != "on")
            {
                return Results.LocalRedirect("/register?error=terms");
            }

            if (auth.Value.Mode == AuthenticationMode.Development)
            {
                return Results.LocalRedirect(returnUrl);
            }

            var properties = new AuthenticationProperties { RedirectUri = returnUrl };
            if (AuthRedirect.CleanEmail(form["email"]) is { } email)
            {
                properties.SetParameter("login_hint", email);
            }

            if (register)
            {
                properties.SetParameter("prompt", "create");
            }

            return Results.Challenge(properties, [OidcScheme]);
        });

        group.MapGet("/signout", (IOptions<InsightFlowAuthOptions> auth) =>
            auth.Value.Mode == AuthenticationMode.Development
                ? Results.LocalRedirect("/login?signedout=1")
                : Results.SignOut(new AuthenticationProperties { RedirectUri = "/login?signedout=1" }, [CookieScheme, OidcScheme]));

        return app;
    }
}

/// <summary>Checks for values that arrive from the browser before they are used in a redirect or sent to the identity provider.</summary>
public static class AuthRedirect
{
    private static readonly string[] Loops = ["/login", "/register", "/auth"];

    /// <summary>A same-site path to go to after signing in; anything else (another site, a scheme-relative address, the sign-in pages themselves) becomes the home page.</summary>
    public static string Safe(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl.Length > 2000 || returnUrl[0] != '/'
            || (returnUrl.Length > 1 && returnUrl[1] is '/' or '\\')
            || returnUrl.Any(char.IsControl)
            || Loops.Any(l => returnUrl.StartsWith(l, StringComparison.OrdinalIgnoreCase)))
        {
            return "/";
        }

        return returnUrl;
    }

    /// <summary>The email as an address, or null when it is empty or malformed (the identity provider then simply asks for it).</summary>
    public static string? CleanEmail(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 254 || !MailAddress.TryCreate(text, out var address) || address.Address != text)
        {
            return null;
        }

        return text;
    }
}
