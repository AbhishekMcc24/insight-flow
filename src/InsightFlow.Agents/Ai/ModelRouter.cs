using InsightFlow.Domain.Tenancy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InsightFlow.Agents.Ai;

/// <summary>A resolved route: provider, model size and the concrete model/deployment id.</summary>
public sealed record ModelRoute(string Provider, string Size, string ModelId)
{
    public string Key => $"{Provider}:{Size}";
}

/// <summary>Thrown when no configured provider can serve a task (no keys set). Mapped to 503 by AgentService.</summary>
public sealed class AiNotConfiguredException : Exception
{
    public AiNotConfiguredException()
        : base("No AI provider is configured. Set an Anthropic or Azure OpenAI key (see README, 'AI provider keys').")
    {
    }

    public AiNotConfiguredException(string message)
        : base(message)
    {
    }

    public AiNotConfiguredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Chooses the model for a tenant and task (D8): tenant override → default route → any configured provider of the
/// same size. Returned clients are wrapped in <see cref="TokenBudgetChatClient"/> for that tenant.
/// </summary>
public interface IModelRouter
{
    /// <summary>Route keys that have a registered client (e.g. <c>anthropic:large</c>).</summary>
    IReadOnlyCollection<string> AvailableRoutes { get; }

    ModelRoute Resolve(TenantId tenant, AiTask task);

    IChatClient GetClient(TenantId tenant, AiTask task);
}

internal sealed class ModelRouter(
    IServiceProvider services,
    IOptions<AiOptions> options,
    ITokenUsageStore usage,
    TimeProvider clock,
    AvailableModelRoutes available) : IModelRouter
{
    public IReadOnlyCollection<string> AvailableRoutes => [.. available.Routes.Keys];

    public ModelRoute Resolve(TenantId tenant, AiTask task)
    {
        var settings = options.Value;
        var requested = settings.TenantRoutes.TryGetValue(tenant.ToString(), out var overrides) && overrides.TryGetValue(task, out var o)
            ? o
            : settings.Routes.GetValueOrDefault(task, "anthropic:large");

        if (available.Routes.TryGetValue(requested, out var route))
        {
            return route;
        }

        // Fall back to any configured provider with the same model size, then to anything configured.
        var size = requested.Split(':', 2) is [_, var s] ? s : "large";
        return available.Routes.Values.FirstOrDefault(r => r.Size == size)
               ?? available.Routes.Values.FirstOrDefault()
               ?? throw new AiNotConfiguredException();
    }

    public IChatClient GetClient(TenantId tenant, AiTask task)
    {
        var route = Resolve(tenant, task);
        var inner = services.GetRequiredKeyedService<IChatClient>(route.Key);
        var budget = options.Value.TenantMonthlyTokenBudgets.GetValueOrDefault(tenant.ToString(), options.Value.DefaultMonthlyTokenBudget);
        return new TokenBudgetChatClient(inner, tenant, route, budget, usage, clock);
    }
}

/// <summary>The routes registered at startup (only providers with credentials).</summary>
internal sealed class AvailableModelRoutes(IReadOnlyDictionary<string, ModelRoute> routes)
{
    public IReadOnlyDictionary<string, ModelRoute> Routes { get; } = routes;
}
