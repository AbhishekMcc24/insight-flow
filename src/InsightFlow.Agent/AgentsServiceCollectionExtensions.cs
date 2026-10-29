using InsightFlow.Agent.Ai;
using InsightFlow.Agent.Analyst;
using InsightFlow.Agent.DerivedFields;
using InsightFlow.Agent.Knowledge;
using InsightFlow.Agent.Sandbox;
using MCC.Foundation.Guardrails.Configuration;
using MCC.Foundation.Guardrails.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InsightFlow.Agent;

/// <summary>Registers the sandbox, model router/providers, the Analyst agent and the derived-field planner. Requires the query engine (extract store).</summary>
public static class AgentsServiceCollectionExtensions
{
    public static IServiceCollection AddInsightFlowAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SandboxOptions>()
            .Bind(configuration.GetSection(SandboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<InsightFlow.Agent.Documents.SheetQaOptions>().Bind(configuration.GetSection(InsightFlow.Agent.Documents.SheetQaOptions.SectionName));
        // The guardrail layer: local checks for vulgarity and prompt injection, run before any model is called.
        services.AddOptions<InsightFlow.Agent.Guardrails.InputGuardOptions>().Bind(configuration.GetSection(InsightFlow.Agent.Guardrails.InputGuardOptions.SectionName));
        services.AddMccGuardrails(GuardrailCategory.SlangProfanity | GuardrailCategory.PromptInjection | GuardrailCategory.Jailbreak, _ => { });
        services.TryAddSingleton<InsightFlow.Agent.Guardrails.IInputGuard, InsightFlow.Agent.Guardrails.MccInputGuard>();

        services.TryAddSingleton<ISqlSandbox, DuckDbSqlSandbox>();
        services.AddInsightFlowAi(configuration);
        services.TryAddSingleton<AnalystAgent>();
        services.TryAddSingleton<DerivedFieldPlanner>();
        services.TryAddSingleton<KnowledgeGraphPlanner>();
        services.TryAddSingleton<KnowledgeAssistant>();
        services.TryAddSingleton<GraphQueryPlanner>();
        services.AddOptions<ConversationOptions>().Bind(configuration.GetSection(ConversationOptions.SectionName)).ValidateDataAnnotations();
        services.TryAddSingleton<ConversationCompactor>();
        services.TryAddSingleton<TablePicker>();
        services.TryAddSingleton<InsightFlow.Agent.Skills.SkillPicker>();
        services.TryAddSingleton<InsightFlow.Agent.Skills.SkillAssistant>();
        services.TryAddSingleton<InsightFlow.Agent.Documents.SheetQuestionAnswerer>();
        return services;
    }
}
