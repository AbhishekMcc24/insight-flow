using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace InsightFlow.Agent.Ai;

/// <summary>Tokens used by the model calls of one step, and the model that answered (the last one, when several calls were made).</summary>
public sealed record AiUsage(long InputTokens, long OutputTokens, int Calls, string? Model)
{
    public static AiUsage None { get; } = new(0, 0, 0, null);

    public long TotalTokens => InputTokens + OutputTokens;

    public static AiUsage operator +(AiUsage a, AiUsage b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.Calls + b.Calls, b.Model ?? a.Model);
    }
}

/// <summary>Adds up the usage the model reports for each call of one step (repairs and retries count too).</summary>
public sealed class UsageTally
{
    private long _input;
    private long _output;
    private int _calls;
    private string? _model;

    public void Add(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _calls++;
        _input += response.Usage?.InputTokenCount ?? 0;
        _output += response.Usage?.OutputTokenCount ?? 0;
        _model = response.ModelId ?? _model;
    }

    public AiUsage Snapshot() => new(_input, _output, _calls, _model);
}

/// <summary>Price of a model in US dollars per million tokens.</summary>
public sealed class ModelPrice
{
    public decimal InputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }
}

/// <summary>
/// Turns token counts into an estimated cost. Prices are list prices per million tokens, matched by the longest model-name prefix
/// (so "gpt-4.1-2025-04-14" uses "gpt-4.1"). They can be changed or added under <c>Ai:Pricing</c>. A model with no price gives no cost
/// (null) instead of a guess.
/// </summary>
public sealed class ModelPriceBook(IOptions<AiOptions> options)
{
    private static readonly Dictionary<string, ModelPrice> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4.1-nano"] = new() { InputPerMillion = 0.10m, OutputPerMillion = 0.40m },
        ["gpt-4.1-mini"] = new() { InputPerMillion = 0.40m, OutputPerMillion = 1.60m },
        ["gpt-4.1"] = new() { InputPerMillion = 2.00m, OutputPerMillion = 8.00m },
        ["gpt-4o-mini"] = new() { InputPerMillion = 0.15m, OutputPerMillion = 0.60m },
        ["gpt-4o"] = new() { InputPerMillion = 2.50m, OutputPerMillion = 10.00m },
        ["gpt-5-mini"] = new() { InputPerMillion = 0.25m, OutputPerMillion = 2.00m },
        ["gpt-5"] = new() { InputPerMillion = 1.25m, OutputPerMillion = 10.00m },
        ["claude-haiku-4-5"] = new() { InputPerMillion = 1.00m, OutputPerMillion = 5.00m },
    };

    public ModelPrice? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var configured = options.Value.Pricing;
        return Longest(configured, model) ?? Longest(Defaults, model);
    }

    /// <summary>The cost of <paramref name="usage"/>, or null when the model has no price. Nothing used costs nothing.</summary>
    public decimal? CostOf(AiUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.TotalTokens == 0)
        {
            return 0m;
        }

        return Find(usage.Model) is { } price
            ? (usage.InputTokens * price.InputPerMillion + usage.OutputTokens * price.OutputPerMillion) / 1_000_000m
            : null;
    }

    /// <summary>The model a task goes to for a tenant (from the route and the provider's size → model map), for forecasting a cost before a call is made.</summary>
    public string? ModelFor(string tenant, AiTask task)
    {
        var o = options.Value;
        var route = o.TenantRoutes.TryGetValue(tenant, out var overrides) && overrides.TryGetValue(task, out var own) ? own : o.Routes.GetValueOrDefault(task);
        var parts = route?.Split(':', 2);
        if (parts is not { Length: 2 })
        {
            return null;
        }

        return parts[0].ToLowerInvariant() switch
        {
            "azure-openai" => o.AzureOpenAI.Deployments.GetValueOrDefault(parts[1]),
            "anthropic" => o.Anthropic.Models.GetValueOrDefault(parts[1]),
            _ => null,
        };
    }

    private static readonly Dictionary<string, int> DefaultWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4.1"] = 1_047_576,
        ["gpt-4o"] = 128_000,
        ["gpt-5"] = 400_000,
        ["claude-"] = 200_000,
    };

    /// <summary>Tokens of context the model can hold (configured, else a built-in value for common models, else a cautious 128,000).</summary>
    public int ContextWindowOf(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return 128_000;
        }

        static int? Longest(IReadOnlyDictionary<string, int> windows, string model) =>
            windows.Where(w => model.StartsWith(w.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(w => w.Key.Length).Select(w => (int?)w.Value).FirstOrDefault();

        return Longest(options.Value.ContextWindows, model) ?? Longest(DefaultWindows, model) ?? 128_000;
    }

    /// <summary>Whether a request of this size should be confirmed first: by cost when the model is priced, by token count otherwise.</summary>
    public bool IsLarge(decimal? cost, long totalTokens) =>
        cost is { } c ? c >= options.Value.WarnAboveCostUsd : totalTokens >= options.Value.WarnAboveTokens;

    private static ModelPrice? Longest(IReadOnlyDictionary<string, ModelPrice> prices, string model) =>
        prices.Where(p => model.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Key.Length).Select(p => p.Value).FirstOrDefault();
}
