using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Blobs;
using InsightFlow.Contracts;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Viz;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using InsightFlow.Testing;

namespace InsightFlow.IntegrationTests;

/// <summary>
/// End to end through QueryService: a Parquet extract in Azurite + a dataset version row → chart query over HTTP,
/// served from Redis on repeat, invisible to other tenants.
/// </summary>
public sealed class QueryServiceTests(AppHostFixture fixture)
{
    private const int Rows = 2_000;
    private static readonly TenantId Contoso = new(DevelopmentIdentity.TenantId);

    private static readonly DatasetSchema RetailSchema = new(
    [
        new SchemaColumn("order_id", DataType.Integer), new SchemaColumn("order_date", DataType.Date), new SchemaColumn("channel", DataType.String),
        new SchemaColumn("store_name", DataType.String), new SchemaColumn("region", DataType.String), new SchemaColumn("country", DataType.String),
        new SchemaColumn("product_name", DataType.String), new SchemaColumn("category", DataType.String), new SchemaColumn("subcategory", DataType.String),
        new SchemaColumn("quantity", DataType.Integer), new SchemaColumn("revenue", DataType.Decimal), new SchemaColumn("cost", DataType.Decimal),
        new SchemaColumn("is_returned", DataType.Boolean),
    ]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Uploads a fresh denormalized retail extract for Contoso and registers its dataset version.</summary>
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

        var version = DatasetVersion.CreateSource(id, Contoso, RetailSchema, Rows, DevelopmentIdentity.UserId, DateTimeOffset.UtcNow);
        await using var db = fixture.CreateDbContext(new FixedCurrentTenant(Contoso));
        db.DatasetVersions.Add(version);
        await db.SaveChangesAsync(Ct);
        return version;
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, string? devToken = null)
    {
        using var client = fixture.CreateHttpClient("queryservice");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, typeInfo) };
        if (devToken is not null)
        {
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(devToken);
        }

        return await client.SendAsync(request, Ct);
    }

    private static VizSpec RevenueByRegion(Guid versionId) =>
        new(1, versionId, Mark.Bar, new VizEncoding(new FieldRef("region"), new FieldRef("revenue", Agg.Sum)), [new EqualsFilter("channel", "Online")]);

    [Fact]
    public async Task Viz_RunsOnDuckDb_ThenServesFromCache()
    {
        fixture.RequireRunning();
        var version = await CreateRetailVersionAsync();
        var request = new VizQueryRequest(RevenueByRegion(version.Id));

        using var first = await PostAsync("/api/v1/query/viz", request, ContractsJsonContext.Default.VizQueryRequest);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        var r1 = (await first.Content.ReadFromJsonAsync(ContractsJsonContext.Default.VizQueryResponse, Ct))!;

        using var second = await PostAsync("/api/v1/query/viz", request, ContractsJsonContext.Default.VizQueryRequest);
        var r2 = (await second.Content.ReadFromJsonAsync(ContractsJsonContext.Default.VizQueryResponse, Ct))!;

        r1.FromCache.ShouldBeFalse();
        r2.FromCache.ShouldBeTrue();
        r1.Result.Rows.Count.ShouldBe(RetailDataGenerator.Regions.Length);
        r1.Result.Columns.Select(c => c.Channel).ShouldBe(["x", "y"]);
        r1.Sql.ShouldContain("WHERE t0.\"channel\" = $p0");
        JsonSerializer.Serialize(r2.Result.Rows).ShouldBe(JsonSerializer.Serialize(r1.Result.Rows));
    }

    [Fact]
    public async Task Viz_OtherTenant_GetsNotFound()
    {
        fixture.RequireRunning();
        var version = await CreateRetailVersionAsync();
        var outsider = new DevToken("eve", Guid.NewGuid(), [InsightFlowRoles.TenantAdmin]).ToHeaderValue();

        using var response = await PostAsync("/api/v1/query/viz", new VizQueryRequest(RevenueByRegion(version.Id)), ContractsJsonContext.Default.VizQueryRequest, outsider);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Viz_InvalidSpec_Returns422WithErrorCodes()
    {
        fixture.RequireRunning();
        var version = await CreateRetailVersionAsync();
        var spec = new VizSpec(1, version.Id, Mark.Bar, new VizEncoding(new FieldRef("nope"), new FieldRef("channel", Agg.Sum)), []);

        using var response = await PostAsync("/api/v1/query/viz", new VizQueryRequest(spec), ContractsJsonContext.Default.VizQueryRequest);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("unknown_field");
        body.ShouldContain("illegal_aggregation");
    }

    [Fact]
    public async Task Preview_ReturnsRowsAndSchema()
    {
        fixture.RequireRunning();
        var version = await CreateRetailVersionAsync();

        using var response = await PostAsync("/api/v1/query/preview", new PreviewRequest(version.Id, 10), ContractsJsonContext.Default.PreviewRequest);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.PreviewResponse, Ct))!;
        preview.TotalRowCount.ShouldBe(Rows);
        preview.Result.Rows.Count.ShouldBe(10);
        preview.Result.Columns.Single(c => c.Name == "order_date").Type.ShouldBe(ColumnType.Date);
    }

    [Fact]
    public async Task Preview_AsViewer_IsForbidden()
    {
        fixture.RequireRunning();
        var viewer = new DevToken("vic", DevelopmentIdentity.TenantId, [InsightFlowRoles.Viewer]).ToHeaderValue();

        using var response = await PostAsync("/api/v1/query/preview", new PreviewRequest(Guid.NewGuid()), ContractsJsonContext.Default.PreviewRequest, viewer);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
