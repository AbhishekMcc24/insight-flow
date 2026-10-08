using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAI;
using OpenAI.Chat;

namespace InsightFlow.Agents.Ai;

/// <summary>
/// Registers one keyed <see cref="IChatClient"/> per configured <c>provider:size</c> (Azure OpenAI and/or Anthropic, D8) with a
/// shared middleware pipeline, plus the <see cref="IModelRouter"/>. Pipeline (outermost first): distributed response cache
/// (when an <see cref="IDistributedCache"/> exists), OpenTelemetry (no prompt/response content), logging (metadata only).
/// Function invocation is added by the agents themselves; the tenant token budget is applied by the router.
/// </summary>
public static class AiServiceCollectionExtensions
{
    public const string AzureOpenAiProvider = "azure-openai";
    public const string AnthropicProvider = "anthropic";

    public static IServiceCollection AddInsightFlowAi(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AiOptions.SectionName);
        services.AddOptions<AiOptions>().Bind(section);
        var settings = section.Get<AiOptions>() ?? new AiOptions();
        var routes = new Dictionary<string, ModelRoute>(StringComparer.OrdinalIgnoreCase);

        if (settings.Anthropic.IsConfigured)
        {
            var client = new AnthropicClient { ApiKey = settings.Anthropic.ApiKey! };
            foreach (var (size, model) in settings.Anthropic.Models)
            {
                var route = new ModelRoute(AnthropicProvider, size, model);
                routes[route.Key] = route;
                AddPipeline(services, route.Key, _ => client.AsIChatClient(model));
            }
        }

        if (settings.AzureOpenAI.IsConfigured)
        {
            var azure = settings.AzureOpenAI;
            var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(azure.Endpoint!) };
            foreach (var (size, deployment) in azure.Deployments)
            {
                var route = new ModelRoute(AzureOpenAiProvider, size, deployment);
                routes[route.Key] = route;
                // OPENAI001: the token-policy constructor is the Entra ID path Microsoft documents for the v1 endpoint.
#pragma warning disable OPENAI001
                AddPipeline(services, route.Key, _ => (azure.UseEntraId
                    ? new ChatClient(deployment, new BearerTokenPolicy(new DefaultAzureCredential(), "https://ai.azure.com/.default"), clientOptions)
                    : new ChatClient(deployment, new ApiKeyCredential(azure.ApiKey!), clientOptions)).AsIChatClient());
#pragma warning restore OPENAI001
            }
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new AvailableModelRoutes(routes));
        services.TryAddSingleton<ITokenUsageStore>(sp =>
            sp.GetService<IDistributedCache>() is { } cache ? new DistributedCacheTokenUsageStore(cache) : new InMemoryTokenUsageStore());
        services.TryAddSingleton<IModelRouter, ModelRouter>();
        return services;
    }

    private static void AddPipeline(IServiceCollection services, string key, Func<IServiceProvider, IChatClient> factory) =>
        services.AddKeyedChatClient(key, factory)
            .Use((inner, sp) => sp.GetService<IDistributedCache>() is { } cache ? new DistributedCachingChatClient(inner, cache) : inner)
            .UseOpenTelemetry(sourceName: AgentsTelemetry.Name, configure: c => c.EnableSensitiveData = false)
            .UseLogging();
}
