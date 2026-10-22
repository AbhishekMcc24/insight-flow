using InsightFlow.Domain.Threads;
using InsightFlow.Query.Caching;
using InsightFlow.Query.Compilation;
using InsightFlow.Query.Execution;
using InsightFlow.Query.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InsightFlow.Query;

/// <summary>DI registration of the query engine. Requires <c>FileStorage:Root</c> (or connection string <c>storage</c>) and an <c>IDistributedCache</c> (Redis).</summary>
public static class QueryServiceCollectionExtensions
{
    public static IServiceCollection AddInsightFlowQueryEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<QueryEngineOptions>()
            .Bind(configuration.GetSection(QueryEngineOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart()
            .PostConfigure(o => o.StorageRoot = LocalStorage.ChooseRoot(
                configuration[LocalStorage.RootConfigurationKey],
                configuration.GetConnectionString(LocalStorage.ConnectionStringName)));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISqlCompiler, SqlCompiler>();
        services.TryAddSingleton<IExtractStore, DirectoryExtractStore>();
        services.TryAddSingleton<IQueryExecutor, DuckDbQueryExecutor>();
        services.TryAddSingleton<IQueryCache, DistributedQueryCache>();
        services.TryAddSingleton<IVizQueryEngine, VizQueryEngine>();
        return services;
    }
}
