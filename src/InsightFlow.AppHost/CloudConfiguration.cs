namespace InsightFlow.AppHost;

/// <summary>
/// Settings that only apply when publishing to Azure Container Apps (<c>aspire deploy</c> / <c>aspire publish</c>).
/// Nothing here runs on <c>aspire run</c>. See docs/deploy.md.
/// </summary>
internal static class CloudConfiguration
{
    /// <summary>
    /// Deployment parameters for Microsoft Entra External ID. They are prompted for (or read from
    /// <c>Parameters:*</c> configuration) at deploy time; the client secret is a secure parameter.
    /// </summary>
    internal sealed record EntraParameters(
        IResourceBuilder<ParameterResource> Instance,
        IResourceBuilder<ParameterResource> TenantId,
        IResourceBuilder<ParameterResource> ApiClientId,
        IResourceBuilder<ParameterResource> WebClientId,
        IResourceBuilder<ParameterResource> WebClientSecret)
    {
        public static EntraParameters Add(IDistributedApplicationBuilder builder) => new(
            builder.AddParameter("entra-instance", secret: false),
            builder.AddParameter("entra-tenant-id", secret: false),
            builder.AddParameter("entra-api-client-id", secret: false),
            builder.AddParameter("entra-web-client-id", secret: false),
            builder.AddParameter("entra-web-client-secret", secret: true));
    }

    /// <summary>API services validate Entra access tokens issued for the API app registration.</summary>
    public static IResourceBuilder<ProjectResource> WithEntraApi(this IResourceBuilder<ProjectResource> project, EntraParameters? entra)
    {
        if (entra is null)
        {
            return project;
        }

        return project
            .WithEnvironment("AzureAd__Instance", entra.Instance)
            .WithEnvironment("AzureAd__TenantId", entra.TenantId)
            .WithEnvironment("AzureAd__ClientId", entra.ApiClientId)
            .WithEnvironment("AzureAd__Audience", ReferenceExpression.Create($"api://{entra.ApiClientId}"));
    }

    /// <summary>The Web app signs users in (OIDC) and acquires tokens for the API scope on their behalf.</summary>
    public static IResourceBuilder<ProjectResource> WithEntraWebApp(this IResourceBuilder<ProjectResource> project, EntraParameters? entra)
    {
        if (entra is null)
        {
            return project;
        }

        return project
            .WithEnvironment("AzureAd__Instance", entra.Instance)
            .WithEnvironment("AzureAd__TenantId", entra.TenantId)
            .WithEnvironment("AzureAd__ClientId", entra.WebClientId)
            .WithEnvironment("AzureAd__ClientCredentials__0__SourceType", "ClientSecret")
            .WithEnvironment("AzureAd__ClientCredentials__0__ClientSecret", entra.WebClientSecret)
            .WithEnvironment("DownstreamApi__Scopes__0", ReferenceExpression.Create($"api://{entra.ApiClientId}/access_as_user"));
    }

    /// <summary>Replica bounds for the container app (ignored locally).</summary>
    public static IResourceBuilder<ProjectResource> WithReplicas(this IResourceBuilder<ProjectResource> project, int min, int max) =>
        project.PublishAsAzureContainerApp((_, app) =>
        {
            app.Template.Scale.MinReplicas = min;
            app.Template.Scale.MaxReplicas = max;
        });
}
