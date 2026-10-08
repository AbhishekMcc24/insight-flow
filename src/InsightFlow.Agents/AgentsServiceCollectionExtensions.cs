using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Analyst;
using InsightFlow.Agents.DerivedFields;
using InsightFlow.Agents.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InsightFlow.Agents;

/// <summary>Registers the sandbox, model router/providers, the Analyst agent and the derived-field planner. Requires the query engine (extract store).</summary>
public static class AgentsServiceCollectionExtensions
{
    public static IServiceCollection AddInsightFlowAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SandboxOptions>()
            .Bind(configuration.GetSection(SandboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<ISqlSandbox, DuckDbSqlSandbox>();
        services.AddInsightFlowAi(configuration);
        services.TryAddSingleton<AnalystAgent>();
        services.TryAddSingleton<DerivedFieldPlanner>();
        return services;
    }
}
