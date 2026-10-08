extern alias agentservice;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Storage.Blobs;
using InsightFlow.Agents.Ai;
using InsightFlow.Contracts;
using InsightFlow.Contracts.Agents;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Viz;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using InsightFlow.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

using AgentServiceProgram = agentservice::Program;

namespace InsightFlow.IntegrationTests;

/// <summary>
/// AgentService end to end on the real infrastructure started by the AppHost (PostgreSQL, Redis, Azurite), with only
/// the model replaced by a scripted client: the derived-field flow persists a child DatasetVersion + Parquet + Data
/// Thread and charts it; the analyst streams SSE. Without AI keys the real AgentService answers 503.
/// </summary>
public sealed class AgentServiceTests(AppHostFixture fixture)
{
    private const int Rows = 1_000;
    private static readonly TenantId Contoso = new(DevelopmentIdentity.TenantId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RealAgentService_WithoutAiKeys_Returns503()
    {
        fixture.RequireRunning();
        using var client = fixture.CreateHttpClient("agentservice");

        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/agent/analyst", UriKind.Relative),
            new AnalystRequest(Guid.NewGuid(), "Hi"), ContractsJsonContext.Default.AnalystRequest, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task DerivedField_PersistsChildVersionAndThread_AndChartsIt()
    {
        fixture.RequireRunning();
        var source = await CreateRetailVersionAsync();
        var model = new ScriptedModel()
            .ThenText("""{"sql": "SELECT *, revenue - cost AS \"margin\" FROM input", "explanation": "Revenue minus cost for each order line."}""");
        await using var factory = await CreateFactoryAsync(model);
        using var client = factory.CreateClient();

        var spec = new VizSpec(1, source.Id, Mark.Bar, new VizEncoding(new FieldRef("region"), new FieldRef("margin", Agg.Sum)), []);
        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/agent/derived-field", UriKind.Relative),
            new DerivedFieldRequest(spec, "margin", "revenue minus cost"), ContractsJsonContext.Default.DerivedFieldRequest, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var result = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.DerivedFieldResponse, Ct))!;

        result.Sql.ShouldContain("revenue - cost");
        result.Explanation.ShouldStartWith("Revenue minus cost");
        result.Spec.DatasetVersionId.ShouldBe(result.DatasetVersionId);
        result.Preview.Columns.Select(c => c.Name).ShouldContain("margin");
        result.Chart.Result.Rows.Count.ShouldBe(RetailDataGenerator.Regions.Length);
        result.Chart.Sql.ShouldContain("SUM(t0.\"margin\")");

        await using var db = fixture.CreateDbContext(new FixedCurrentTenant(Contoso));
        var derived = await db.DatasetVersions.SingleAsync(v => v.Id == result.DatasetVersionId, Ct);
        derived.Kind.ShouldBe(DatasetVersionKind.Derived);
        derived.ParentIds.ShouldBe([source.Id]);
        derived.SqlText.ShouldBe(result.Sql);
        derived.RowCount.ShouldBe(Rows);
        (await db.ThreadNodes.CountAsync(n => n.ThreadId == result.ThreadId, Ct)).ShouldBe(2);
        (await db.ThreadNodes.SingleAsync(n => n.Id == result.ThreadNodeId, Ct)).VizSpec!.DatasetVersionId.ShouldBe(derived.Id);

        var blobs = new BlobServiceClient(await fixture.App.GetConnectionStringAsync("blobs", Ct));
        (await blobs.GetBlobContainerClient("extracts").GetBlobClient(StoragePaths.Extract(Contoso, derived.Id)).ExistsAsync(Ct)).Value.ShouldBeTrue();
    }

    [Fact]
    public async Task DerivedField_SpecWithOtherProblems_IsRejectedBeforeCallingTheModel()
    {
        fixture.RequireRunning();
        var source = await CreateRetailVersionAsync();
        var model = new ScriptedModel();
        await using var factory = await CreateFactoryAsync(model);
        using var client = factory.CreateClient();

        var spec = new VizSpec(1, source.Id, Mark.Bar, new VizEncoding(new FieldRef("region"), new FieldRef("region", Agg.Sum)), []);
        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/agent/derived-field", UriKind.Relative),
            new DerivedFieldRequest(spec, "margin", "revenue minus cost"), ContractsJsonContext.Default.DerivedFieldRequest, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        model.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Analyst_StreamsServerSentEvents()
    {
        fixture.RequireRunning();
        var source = await CreateRetailVersionAsync();
        var model = new ScriptedModel()
            .ThenToolCall("RunSql", new { sql = "SELECT channel, COUNT(*) AS n FROM input GROUP BY channel" })
            .ThenText("Store has more orders than Online.");
        await using var factory = await CreateFactoryAsync(model);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/agent/analyst", UriKind.Relative),
            new AnalystRequest(source.Id, "Which channel has more orders?"), ContractsJsonContext.Default.AnalystRequest, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("event: sql");
        body.ShouldContain("event: text");
        body.ShouldContain("event: done");
        body.ShouldContain("SELECT channel");
    }

    [Fact]
    public async Task Analyst_OtherTenantsDataset_IsNotFound()
    {
        fixture.RequireRunning();
        var source = await CreateRetailVersionAsync();
        await using var factory = await CreateFactoryAsync(new ScriptedModel());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(
            new DevToken("eve", Guid.NewGuid(), [InsightFlowRoles.TenantAdmin]).ToHeaderValue());

        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/agent/analyst", UriKind.Relative),
            new AnalystRequest(source.Id, "Leak it"), ContractsJsonContext.Default.AnalystRequest, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>AgentService in-process, wired to the AppHost's real Postgres/Redis/Azurite, with the scripted model.</summary>
    private async Task<WebApplicationFactory<AgentServiceProgram>> CreateFactoryAsync(ScriptedModel model)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:insightflow"] = fixture.ConnectionString,
            ["ConnectionStrings:redis"] = await fixture.App.GetConnectionStringAsync("redis", Ct),
            ["ConnectionStrings:blobs"] = await fixture.App.GetConnectionStringAsync("blobs", Ct),
        };

        return new WebApplicationFactory<AgentServiceProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IModelRouter>(new ScriptedRouter(model));
            });
        });
    }

    private async Task<DatasetVersion> CreateRetailVersionAsync()
    {
        var id = DatasetVersion.NewId();
        var file = Path.Combine(Path.GetTempPath(), $"insightflow-it-{id:N}.parquet");
        await RetailDataGenerator.WriteDenormalizedAsync(file, Rows, csv: false, Ct);
        try
        {
            var blobs = new BlobServiceClient(await fixture.App.GetConnectionStringAsync("blobs", Ct));
            var container = blobs.GetBlobContainerClient("extracts");
            await container.CreateIfNotExistsAsync(cancellationToken: Ct);
            await container.GetBlobClient(StoragePaths.Extract(Contoso, id)).UploadAsync(file, Ct);
        }
        finally
        {
            File.Delete(file);
        }

        var schema = new DatasetSchema(
        [
            new SchemaColumn("order_id", DataType.Integer), new SchemaColumn("order_date", DataType.Date), new SchemaColumn("channel", DataType.String),
            new SchemaColumn("store_name", DataType.String), new SchemaColumn("region", DataType.String), new SchemaColumn("country", DataType.String),
            new SchemaColumn("product_name", DataType.String), new SchemaColumn("category", DataType.String), new SchemaColumn("subcategory", DataType.String),
            new SchemaColumn("quantity", DataType.Integer), new SchemaColumn("revenue", DataType.Decimal), new SchemaColumn("cost", DataType.Decimal),
            new SchemaColumn("is_returned", DataType.Boolean),
        ]);
        var version = DatasetVersion.CreateSource(id, Contoso, schema, Rows, DevelopmentIdentity.UserId, DateTimeOffset.UtcNow);
        await using var db = fixture.CreateDbContext(new FixedCurrentTenant(Contoso));
        db.DatasetVersions.Add(version);
        await db.SaveChangesAsync(Ct);
        return version;
    }

    /// <summary>Scripted IChatClient (see InsightFlow.Agents.Tests for the unit-test twin).</summary>
    private sealed class ScriptedModel : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        public int Calls { get; private set; }

        public ScriptedModel ThenText(string text)
        {
            _responses.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
            return this;
        }

        public ScriptedModel ThenToolCall(string tool, object arguments)
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments));
            _responses.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, args)])));
            return this;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedRouter(IChatClient client) : IModelRouter
    {
        private static readonly ModelRoute Route = new("scripted", "large", "scripted");

        public IReadOnlyCollection<string> AvailableRoutes => [Route.Key];

        public ModelRoute Resolve(TenantId tenant, AiTask task) => Route;

        public IChatClient GetClient(TenantId tenant, AiTask task) => client;
    }
}
