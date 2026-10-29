using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace InsightFlow.Agent;

/// <summary>
/// Traces and metrics of agents, the model router and the sandbox (<c>InsightFlow.Agent</c>). Tags carry ids, counts,
/// providers and token usage only — never prompts, SQL literals or data.
/// </summary>
public static class AgentsTelemetry
{
    public const string Name = "InsightFlow.Agent";

    public static readonly ActivitySource ActivitySource = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> SandboxRuns = Meter.CreateCounter<long>(
        "insightflow.sandbox.runs", description: "AI-SQL sandbox executions by status.");

    public static readonly Counter<long> TokensUsed = Meter.CreateCounter<long>(
        "insightflow.ai.tokens", unit: "{token}", description: "Model tokens used, by provider route and direction.");

    public static readonly Counter<long> BudgetRejections = Meter.CreateCounter<long>(
        "insightflow.ai.budget_rejections", description: "Model calls refused because a tenant exceeded its monthly token budget.");
}
