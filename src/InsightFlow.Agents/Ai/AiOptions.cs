namespace InsightFlow.Agents.Ai;

/// <summary>What a model call is for; the router maps each task to a provider and model size.</summary>
public enum AiTask
{
    /// <summary>Cheap, fast classification/routing of a request.</summary>
    Routing,

    /// <summary>Writing DuckDB SQL (needs the strongest model).</summary>
    SqlGeneration,

    ChartRecommendation,

    InsightSummary,
}

/// <summary>
/// AI configuration (section <c>Ai</c>). Providers are optional: only those with credentials are registered. Routes
/// are <c>provider:size</c> strings (e.g. <c>anthropic:large</c>); tenants can override routes and budgets.
/// No local / on-premises models (D8).
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public AzureOpenAiOptions AzureOpenAI { get; set; } = new();

    public AnthropicOptions Anthropic { get; set; } = new();

    /// <summary>Default route per task.</summary>
    public Dictionary<AiTask, string> Routes { get; set; } = new()
    {
        [AiTask.Routing] = "anthropic:small",
        [AiTask.SqlGeneration] = "anthropic:large",
        [AiTask.ChartRecommendation] = "anthropic:large",
        [AiTask.InsightSummary] = "anthropic:large",
    };

    /// <summary>Per-tenant route overrides, keyed by tenant id.</summary>
    public Dictionary<string, Dictionary<AiTask, string>> TenantRoutes { get; set; } = [];

    /// <summary>Default monthly token budget per tenant (input + output).</summary>
    public long DefaultMonthlyTokenBudget { get; set; } = 5_000_000;

    /// <summary>Per-tenant monthly budgets, keyed by tenant id.</summary>
    public Dictionary<string, long> TenantMonthlyTokenBudgets { get; set; } = [];
}

/// <summary>
/// Azure OpenAI through the OpenAI SDK's v1 endpoint (<c>https://&lt;resource&gt;.openai.azure.com/openai/v1/</c>).
/// Without an API key, Microsoft Entra ID (managed identity / DefaultAzureCredential) is used.
/// </summary>
public sealed class AzureOpenAiOptions
{
    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Use Entra ID instead of an API key (recommended in Azure).</summary>
    public bool UseEntraId { get; set; }

    /// <summary>Model size → deployment name.</summary>
    public Dictionary<string, string> Deployments { get; set; } = new()
    {
        ["small"] = "gpt-5-mini",
        ["large"] = "gpt-5",
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && (UseEntraId || !string.IsNullOrWhiteSpace(ApiKey));
}

/// <summary>Anthropic through the official <c>Anthropic</c> SDK.</summary>
public sealed class AnthropicOptions
{
    public string? ApiKey { get; set; }

    /// <summary>Model size → model id. Defaults: the current fast model and the current strongest model.</summary>
    public Dictionary<string, string> Models { get; set; } = new()
    {
        ["small"] = "claude-haiku-4-5",
        ["large"] = "claude-opus-5-5",
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
