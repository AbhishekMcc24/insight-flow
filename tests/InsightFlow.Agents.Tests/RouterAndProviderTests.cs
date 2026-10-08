using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Analyst;
using InsightFlow.Contracts.Agents;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Query.Modeling;
using InsightFlow.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace InsightFlow.Agents.Tests;

public sealed class RouterAndProviderTests(SandboxFixture data) : IClassFixture<SandboxFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Services(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddLogging().AddInsightFlowAi(configuration).BuildServiceProvider();
    }

    [Fact]
    public void Router_NoKeys_HasNoRoutes_AndRefusesToResolve()
    {
        using var services = Services([]);
        var router = services.GetRequiredService<IModelRouter>();

        router.AvailableRoutes.ShouldBeEmpty();
        Should.Throw<AiNotConfiguredException>(() => router.Resolve(RetailModel.Tenant, AiTask.SqlGeneration));
    }

    [Fact]
    public void Router_DefaultRoutes_UseSmallForRoutingAndLargeForSql()
    {
        using var services = Services(new() { ["Ai:Anthropic:ApiKey"] = "test-key-not-used" });
        var router = services.GetRequiredService<IModelRouter>();

        router.Resolve(RetailModel.Tenant, AiTask.Routing).ShouldBe(new ModelRoute("anthropic", "small", "claude-haiku-4-5"));
        router.Resolve(RetailModel.Tenant, AiTask.SqlGeneration).ShouldBe(new ModelRoute("anthropic", "large", "claude-opus-5-5"));
        router.GetClient(RetailModel.Tenant, AiTask.SqlGeneration).ShouldBeOfType<TokenBudgetChatClient>();
    }

    [Fact]
    public void Router_TenantOverride_AndFallbackToConfiguredProvider()
    {
        var tenant = TenantId.New();
        using var services = Services(new()
        {
            ["Ai:Anthropic:ApiKey"] = "k",
            ["Ai:AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/openai/v1/",
            ["Ai:AzureOpenAI:ApiKey"] = "k",
            [$"Ai:TenantRoutes:{tenant}:SqlGeneration"] = "azure-openai:large",
            ["Ai:Routes:InsightSummary"] = "missing-provider:small",
        });
        var router = services.GetRequiredService<IModelRouter>();

        router.Resolve(tenant, AiTask.SqlGeneration).Key.ShouldBe("azure-openai:large");
        router.Resolve(RetailModel.Tenant, AiTask.SqlGeneration).Key.ShouldBe("anthropic:large");
        router.Resolve(RetailModel.Tenant, AiTask.InsightSummary).Size.ShouldBe("small");
    }

    [Fact]
    public async Task Budget_RecordsUsage_AndRefusesOnceSpent()
    {
        var usage = new InMemoryTokenUsageStore();
        var client = new TokenBudgetChatClient(new ScriptedChatClient().ThenText("a", 300, 100).ThenText("b"), RetailModel.Tenant, FixedRouter.Route, 400, usage, TimeProvider.System);

        await client.GetResponseAsync("first", cancellationToken: Ct);
        var month = DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        (await usage.GetUsedAsync(RetailModel.Tenant, month, Ct)).ShouldBe(400);
        await Should.ThrowAsync<TokenBudgetExceededException>(() => client.GetResponseAsync("second", cancellationToken: Ct));
        (await usage.GetUsedAsync(RetailModel.OtherTenant, month, Ct)).ShouldBe(0);
    }

    /// <summary>Optional: runs the analyst against a real provider when ANTHROPIC_API_KEY or Azure OpenAI settings are present.</summary>
    [Fact]
    public async Task RealProvider_Analyst_AnswersWithSql()
    {
        var anthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        Assert.SkipWhen(string.IsNullOrEmpty(anthropic) && (string.IsNullOrEmpty(azureEndpoint) || string.IsNullOrEmpty(azureKey)),
            "Set ANTHROPIC_API_KEY or AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY to run the real-provider test.");

        var settings = new Dictionary<string, string?>
        {
            ["Ai:Anthropic:ApiKey"] = anthropic,
            ["Ai:AzureOpenAI:Endpoint"] = azureEndpoint,
            ["Ai:AzureOpenAI:ApiKey"] = azureKey,
        };
        using var services = Services(settings);
        var analyst = new AnalystAgent(services.GetRequiredService<IModelRouter>(), data.CreateSandbox(), NullLogger<AnalystAgent>.Instance);
        var context = new AnalystContext(RetailModel.Tenant, data.Retail, ImplicitSemanticModel.For(data.Retail));

        var events = new List<AgentEvent>();
        await foreach (var e in analyst.RunAsync(context, "What is the total revenue of the Online channel?", Ct))
        {
            events.Add(e);
        }

        events.ShouldNotContain(e => e.Type == AgentEventTypes.Error);
        events.ShouldContain(e => e.Type == AgentEventTypes.Sql && e.Error == null);
        events.Last().Type.ShouldBe(AgentEventTypes.Done);
    }
}
