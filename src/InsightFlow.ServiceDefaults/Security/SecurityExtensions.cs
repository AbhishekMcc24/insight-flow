using InsightFlow.ServiceDefaults.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Authentication, tenant resolution and authorization policies for Insight Flow API services. One call wires:
/// the configured scheme (Entra External ID JWT bearer, or the development handler), <see cref="ITenantContext"/>,
/// and a fallback policy that rejects any request without a valid tenant claim (health endpoints are anonymous).
/// </summary>
public static class SecurityExtensions
{
    public static TBuilder AddInsightFlowSecurity<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder =>
        builder.AddInsightFlowSecurityCore(webApp: false);

    /// <summary>
    /// Security for the Blazor Web app: the development handler locally; in the cloud, Entra External ID sign-in
    /// (OpenID Connect + cookie) with token acquisition so the app can call the services as the signed-in user
    /// (scopes from <c>DownstreamApi:Scopes</c>). Tenant resolution and policies are identical to the APIs.
    /// </summary>
    public static TBuilder AddInsightFlowWebSecurity<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder =>
        builder.AddInsightFlowSecurityCore(webApp: true);

    private static TBuilder AddInsightFlowSecurityCore<TBuilder>(this TBuilder builder, bool webApp)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection(InsightFlowAuthOptions.SectionName);
        builder.Services.AddOptions<InsightFlowAuthOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var auth = section.Get<InsightFlowAuthOptions>() ?? new InsightFlowAuthOptions();

        if (auth.Mode == AuthenticationMode.Development)
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Authentication:Mode=Development is only allowed in the Development environment.");
            }

            builder.Services.AddAuthentication(DevToken.Scheme)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevToken.Scheme, null);
        }
        else if (webApp)
        {
            // TODO(dev2): verify the Entra External ID app registrations (web app + API scope) end to end in a test tenant.
            var scopes = builder.Configuration.GetSection("DownstreamApi:Scopes").Get<string[]>() ?? [];
            builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
                .EnableTokenAcquisitionToCallDownstreamApi(scopes)
                .AddInMemoryTokenCaches();
        }
        else
        {
            // Microsoft Entra External ID (CIAM). Settings live in the "AzureAd" section; see appsettings.json placeholders.
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
        }

        builder.Services.AddAuthorization(options => InsightFlowPolicies.Configure(options, auth));
        builder.Services.AddHttpContextAccessor();
        builder.Services.TryAddScoped<ITenantContext, ClaimsTenantContext>();

        return builder;
    }

    /// <summary>Adds authentication and authorization middleware in the right order.</summary>
    public static WebApplication UseInsightFlowSecurity(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>The effective auth options (for diagnostics and tests).</summary>
    public static InsightFlowAuthOptions GetInsightFlowAuthOptions(this IServiceProvider services) =>
        services.GetRequiredService<IOptions<InsightFlowAuthOptions>>().Value;
}
