using System.Runtime.CompilerServices;
using System.Text.Json;
using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Sandbox;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Query.Storage;
using InsightFlow.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InsightFlow.Agents.Tests;

/// <summary>
/// Deterministic stand-in for a model: returns queued responses in order and records every request it received.
/// Function calls in a scripted response are executed by the agent's function-invocation middleware, exactly as with a
/// real provider, so tools, the sandbox and the event stream are exercised for real.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new();

    public List<List<ChatMessage>> Requests { get; } = [];

    public ScriptedChatClient Then(ChatResponse response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public ScriptedChatClient ThenText(string text, long inputTokens = 100, long outputTokens = 20) =>
        Then(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens, TotalTokenCount = inputTokens + outputTokens },
        });

    public ScriptedChatClient ThenToolCall(string tool, object arguments) =>
        Then(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments)))])));

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add([.. messages]);
        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new ChatResponse(new ChatMessage(ChatRole.Assistant, "(script exhausted)")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Router that always returns one client (wrapped in the real budget enforcement).</summary>
public sealed class FixedRouter(IChatClient client, ITokenUsageStore? usage = null, long budget = long.MaxValue) : IModelRouter
{
    public static readonly ModelRoute Route = new("scripted", "large", "scripted-model");

    public ITokenUsageStore Usage { get; } = usage ?? new InMemoryTokenUsageStore();

    public IReadOnlyCollection<string> AvailableRoutes => [Route.Key];

    public ModelRoute Resolve(TenantId tenant, AiTask task) => Route;

    public IChatClient GetClient(TenantId tenant, AiTask task) => new TokenBudgetChatClient(client, tenant, Route, budget, Usage, TimeProvider.System);
}

/// <summary>Generates the denormalized retail Parquet once and serves it as dataset version(s) to the sandbox.</summary>
public sealed class SandboxFixture : IAsyncLifetime
{
    public const int Rows = 2_000;

    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "insightflow-agents-tests", Guid.NewGuid().ToString("N"));

    public DatasetVersion Retail { get; private set; } = null!;

    public LocalFileExtractStore Store { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var path = Path.Combine(Directory, "retail.parquet");
        await RetailDataGenerator.WriteDenormalizedAsync(path, Rows, csv: false);
        Retail = DatasetVersion.CreateSource(Guid.NewGuid(), RetailModel.Tenant, new DatasetSchema(
        [
            new SchemaColumn("order_id", DataType.Integer), new SchemaColumn("order_date", DataType.Date), new SchemaColumn("channel", DataType.String),
            new SchemaColumn("store_name", DataType.String), new SchemaColumn("region", DataType.String), new SchemaColumn("country", DataType.String),
            new SchemaColumn("product_name", DataType.String), new SchemaColumn("category", DataType.String), new SchemaColumn("subcategory", DataType.String),
            new SchemaColumn("quantity", DataType.Integer), new SchemaColumn("revenue", DataType.Decimal), new SchemaColumn("cost", DataType.Decimal),
            new SchemaColumn("is_returned", DataType.Boolean),
        ]), Rows, "test", DateTimeOffset.UtcNow);
        Store = new LocalFileExtractStore(Directory, new Dictionary<Guid, string> { [Retail.Id] = path });
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    public DuckDbSqlSandbox CreateSandbox(SandboxOptions? options = null) =>
        new(Store, Options.Create(options ?? new SandboxOptions { WorkDirectory = Path.Combine(Directory, "work") }), NullLogger<DuckDbSqlSandbox>.Instance);
}

public sealed class LocalFileExtractStore(string root, IReadOnlyDictionary<Guid, string> files) : IExtractStore
{
    public string LocalRoot => root;

    public Task<string> GetLocalPathAsync(DatasetVersion version, CancellationToken cancellationToken) => Task.FromResult(files[version.Id]);

    public Task<Uri> SaveAsync(TenantId tenant, Guid datasetVersionId, Stream parquet, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
