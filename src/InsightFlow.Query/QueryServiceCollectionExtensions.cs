using InsightFlow.Query.Caching;
using InsightFlow.Query.Compilation;
using InsightFlow.Query.Execution;
using InsightFlow.Query.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InsightFlow.Query;

/// <summary>DI registration of the query engine. Requires a <c>BlobServiceClient</c> and an <c>IDistributedCache</c> (Redis).</summary>
public static class QueryServiceCollectionExtensions
{
    public static IServiceCollection AddInsightFlowQueryEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<QueryEngineOptions>()
            .Bind(configuration.GetSection(QueryEngineOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISqlCompiler, SqlCompiler>();
        services.TryAddSingleton<IExtractStore, BlobExtractStore>();
        services.TryAddSingleton<IQueryExecutor, DuckDbQueryExecutor>();
        services.TryAddSingleton<IQueryCache, DistributedQueryCache>();
        services.TryAddSingleton<IVizQueryEngine, VizQueryEngine>();
        return services;
    }
}
