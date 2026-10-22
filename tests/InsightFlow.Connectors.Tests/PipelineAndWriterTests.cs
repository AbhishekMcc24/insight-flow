using InsightFlow.Connectors.Extraction;
using InsightFlow.Connectors.Files;
using InsightFlow.Connectors.Stubs;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Threads;
using InsightFlow.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace InsightFlow.Connectors.Tests;

public sealed class PipelineAndWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SchemaColumn[] Columns =
    [
        new("id", DataType.Integer), new("name", DataType.String), new("amount", DataType.Decimal),
        new("day", DataType.Date), new("at", DataType.DateTime), new("ok", DataType.Boolean),
    ];

    [Fact]
    public async Task Writer_AppendedRows_RoundTripWithTypesAndNulls()
    {
        await using var writer = await DuckDbExtractWriter.CreateAsync(Dir(), "256MB", Ct);
        await writer.BeginTableAsync(Columns, Ct);
        writer.AppendRow([1, "Ann", 10.5m, new DateOnly(2025, 1, 2), new DateTime(2025, 1, 2, 3, 4, 5), true]);
        writer.AppendRow([2L, null, 3, new DateTime(2025, 2, 3), "2025-02-03T04:05:06", false]); // coercions
        writer.AppendRow([3, "Cy", null, null, null, null]);

        var output = await writer.CompleteAsync(Ct);

        output.RowCount.ShouldBe(3);
        output.Schema.Columns.Select(c => (c.Name, c.DataType)).ShouldBe(Columns.Select(c => (c.Name, c.DataType)));
        File.Exists(output.ParquetPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Writer_WrongValueCount_Throws()
    {
        await using var writer = await DuckDbExtractWriter.CreateAsync(Dir(), "256MB", Ct);
        await writer.BeginTableAsync(Columns, Ct);

        Should.Throw<ArgumentException>(() => writer.AppendRow([1, "x"]));
    }

    [Fact]
    public async Task Writer_OneTableOnly()
    {
        await using var writer = await DuckDbExtractWriter.CreateAsync(Dir(), "256MB", Ct);
        await writer.BeginTableAsync(Columns, Ct);

        await Should.ThrowAsync<InvalidOperationException>(() => writer.BeginTableAsync(Columns, Ct));
    }

    [Theory]
    [InlineData("BIGINT", DataType.Integer)]
    [InlineData("DECIMAL(18,2)", DataType.Decimal)]
    [InlineData("DOUBLE", DataType.Decimal)]
    [InlineData("TIMESTAMP WITH TIME ZONE", DataType.DateTime)]
    [InlineData("VARCHAR[]", DataType.Json)]
    [InlineData("STRUCT(a INTEGER)", DataType.Json)]
    [InlineData("UUID", DataType.String)]
    public void MapDuckDbType_CoversCommonTypes(string duckType, DataType expected)
    {
        DuckDbExtractWriter.MapDuckDbType(duckType).ShouldBe(expected);
    }

    [Fact]
    public async Task Pipeline_FirstRunCreatesSource_RefreshCreatesExtractChild()
    {
        var files = new FakeSourceFileAccessor();
        var uploader = new RecordingExtractUploader();
        var pipeline = new ExtractPipeline(
            new ConnectorRegistry([new CsvConnector(files)]),
            uploader,
            Options.Create(new ConnectorOptions { WorkDirectory = Dir() }),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<ExtractPipeline>.Instance);
        var profile = ConnectionProfile.ForStoredFile(RetailModel.Tenant, DataSourceKind.Csv, files.Add((await RetailFiles.GetAsync()).Csv), "sales.csv");

        var first = await pipeline.RunAsync(new ExtractRequest(profile), previous: null, "alice", Ct);
        var second = await pipeline.RunAsync(new ExtractRequest(profile), first.Version, "worker", Ct);

        first.Version.Kind.ShouldBe(DatasetVersionKind.Source);
        first.Version.RowCount.ShouldBe(RetailFiles.Rows);
        first.Version.TenantId.ShouldBe(RetailModel.Tenant);
        second.Version.Kind.ShouldBe(DatasetVersionKind.Extract);
        second.Version.ParentIds.ShouldBe([first.Version.Id]);
        uploader.Uploads.Select(u => u.VersionId).ShouldBe([first.Version.Id, second.Version.Id]);
        uploader.Uploads.ShouldAllBe(u => u.Bytes > 0);
    }

    [Fact]
    public async Task Pipeline_PreviousFromOtherTenant_IsRefused()
    {
        var pipeline = new ExtractPipeline(new ConnectorRegistry([]), new RecordingExtractUploader(),
            Options.Create(new ConnectorOptions { WorkDirectory = Dir() }), TimeProvider.System, NullLogger<ExtractPipeline>.Instance);
        var foreign = DatasetVersion.CreateSource(Guid.NewGuid(), RetailModel.OtherTenant, DatasetSchema.Empty, 0, "x", DateTimeOffset.UtcNow);
        var profile = ConnectionProfile.ForStoredFile(RetailModel.Tenant, DataSourceKind.Csv, Guid.NewGuid(), "f.csv");

        await Should.ThrowAsync<InvalidOperationException>(() => pipeline.RunAsync(new ExtractRequest(profile), foreign, "x", Ct));
    }

    [Fact]
    public void RegisteredConnectors_AreImplemented()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddInsightFlowConnectors(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var stubs = services
            .Where(s => s.ServiceType == typeof(IDataSourceConnector))
            .Select(s => s.ImplementationType!)
            .Where(t => typeof(NotImplementedConnector).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        stubs.ShouldBeEmpty();
    }

    [Fact]
    public void Registry_KnowsEveryDataSourceKind_WhenFullyRegistered()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddInsightFlowConnectors(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var kinds = services.Where(s => s.ServiceType == typeof(IDataSourceConnector)).Select(s => s.ImplementationType!).ToList();

        kinds.Count.ShouldBe(Enum.GetValues<DataSourceKind>().Length);
    }

    private static string Dir() => Path.Combine(Path.GetTempPath(), "insightflow-connector-tests", Guid.NewGuid().ToString("N"));
}
