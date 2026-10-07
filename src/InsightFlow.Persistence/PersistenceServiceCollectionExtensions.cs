using Azure.Security.KeyVault.Secrets;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Persistence.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InsightFlow.Persistence;

/// <summary>DI registration for the metadata store and secret store.</summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="InsightFlowDbContext"/> (scoped, not pooled: it depends on the per-request tenant) and an
    /// <see cref="ICurrentTenant"/> built from <paramref name="resolveTenant"/>. Hosts using Aspire call
    /// <c>builder.EnrichNpgsqlDbContext&lt;InsightFlowDbContext&gt;()</c> afterwards for retries, health checks and telemetry.
    /// </summary>
    public static IServiceCollection AddInsightFlowPersistence(
        this IServiceCollection services,
        string? connectionString,
        Func<IServiceProvider, TenantId?> resolveTenant)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        services.TryAddScoped<ICurrentTenant>(sp => new DelegateCurrentTenant(() => resolveTenant(sp)));
        return services.AddInsightFlowDbContext(connectionString);
    }

    /// <summary>Registers the DbContext for trusted system work (migrations, seeding). Never use in a request-serving host.</summary>
    public static IServiceCollection AddInsightFlowPersistenceForSystem(this IServiceCollection services, string? connectionString)
    {
        services.TryAddSingleton<ICurrentTenant>(SystemCurrentTenant.Instance);
        return services.AddInsightFlowDbContext(connectionString);
    }

    private static IServiceCollection AddInsightFlowDbContext(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'insightflow' is missing. Run through the Aspire AppHost or set ConnectionStrings:insightflow.");
        }

        services.AddDbContext<InsightFlowDbContext>(options => options.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history")));
        return services;
    }

    /// <summary>
    /// Registers <see cref="ISecretStore"/>: Azure Key Vault when a <see cref="SecretClient"/> is registered (cloud,
    /// via <c>AddAzureKeyVaultClient</c>), otherwise the development file store at <paramref name="localFilePath"/>.
    /// </summary>
    public static IServiceCollection AddInsightFlowSecretStore(this IServiceCollection services, bool useKeyVault, string localFilePath)
    {
        if (useKeyVault)
        {
            services.TryAddSingleton<ISecretStore, KeyVaultSecretStore>();
        }
        else
        {
            services.TryAddSingleton<ISecretStore>(_ => new LocalFileSecretStore(localFilePath));
        }

        return services;
    }
}
