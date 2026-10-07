using Aspire.Hosting.Azure;

namespace InsightFlow.AppHost;

internal static class ResourceBuilderExtensions
{
    /// <summary>
    /// References Key Vault only when it exists (publish mode). Locally services use
    /// user-secrets / a git-ignored file through <c>ISecretStore</c> instead.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithKeyVault(
        this IResourceBuilder<ProjectResource> project,
        IResourceBuilder<AzureKeyVaultResource>? keyVault) =>
        keyVault is null ? project : project.WithReference(keyVault);

    /// <summary>
    /// Passes an Aspire parameter to the project as an environment variable. When publishing, the parameter is
    /// always declared (it becomes a deployment input). Locally it is only wired when a value is configured
    /// (<c>Parameters:&lt;name&gt;</c> in AppHost user-secrets), so a clone without AI keys still starts cleanly.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithOptionalParameter(
        this IResourceBuilder<ProjectResource> project,
        string environmentVariable,
        string parameterName,
        bool secret)
    {
        var builder = project.ApplicationBuilder;
        var configured = !string.IsNullOrWhiteSpace(builder.Configuration[$"Parameters:{parameterName}"]);
        if (!builder.ExecutionContext.IsPublishMode && !configured)
        {
            return project;
        }

        var parameter = builder.AddParameter(parameterName, secret: secret);
        return project.WithEnvironment(environmentVariable, parameter);
    }
}
