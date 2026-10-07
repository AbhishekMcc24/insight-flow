using InsightFlow.Connectors.Files;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Modeling;
using InsightFlow.Testing;

namespace InsightFlow.Connectors.Tests;

public sealed class CsvConnectorTests : ConnectorContractTests
{
    private readonly FakeSourceFileAccessor _files = new();

    protected override DataSourceKind ExpectedKind => DataSourceKind.Csv;

    protected override string ExpectedTableId => FileConnectorBase.FileTableId;

    protected override long ExpectedRowCount => RetailFiles.Rows;

    protected override IReadOnlyCollection<string> ExpectedColumns => ["order_id", "order_date", "region", "revenue", "is_returned"];

    protected override IDataSourceConnector CreateConnector() => new CsvConnector(_files);

    protected override async Task<ConnectionProfile> CreateValidProfileAsync()
    {
        var (csv, _) = await RetailFiles.GetAsync();
        return ConnectionProfile.ForStoredFile(RetailModel.Tenant, DataSourceKind.Csv, _files.Add(csv), "retail_sales.csv");
    }

    protected override ConnectionProfile CreateUnreachableProfile() =>
        ConnectionProfile.ForStoredFile(RetailModel.Tenant, DataSourceKind.Csv, Guid.NewGuid(), "missing.csv");

    [Fact]
    public async Task Extract_Csv_InfersLogicalTypes()
    {
        var output = await ExtractCsvAsync(await File.ReadAllTextAsync((await RetailFiles.GetAsync()).Csv, Ct));
        var types = output.Schema.Columns.ToDictionary(c => c.Name, c => c.DataType);

        types["order_id"].ShouldBe(DataType.Integer);
        types["order_date"].ShouldBe(DataType.Date);
        types["revenue"].ShouldBe(DataType.Decimal);
        types["is_returned"].ShouldBe(DataType.Boolean);
        types["region"].ShouldBe(DataType.String);
    }

    [Fact]
    public async Task Extract_QuotedFieldsAndOddHeaders_AreKept()
    {
        const string csv = "\"Customer, Name\",Amount €,\"note \"\"x\"\"\"\n\"Ann, A\",10.5,\"a,b\"\nBob,3,\n";

        var output = await ExtractCsvAsync(csv);

        output.RowCount.ShouldBe(2);
        output.Schema.Columns.Select(c => c.Name).ShouldBe(["Customer, Name", "Amount €", "note \"x\""]);
    }

    [Fact]
    public async Task Extract_SemicolonDelimited_IsDetected()
    {
        var output = await ExtractCsvAsync("a;b\n1;x\n2;y\n");

        output.Schema.Columns.Select(c => c.Name).ShouldBe(["a", "b"]);
        output.RowCount.ShouldBe(2);
    }

    private async Task<Extraction.ExtractOutput> ExtractCsvAsync(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"insightflow-csv-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, content, Ct);
        try
        {
            var profile = ConnectionProfile.ForStoredFile(RetailModel.Tenant, DataSourceKind.Csv, _files.Add(path), "f.csv");
            await using var writer = await Extraction.DuckDbExtractWriter.CreateAsync(NewWorkDirectory(), "256MB", Ct);
            await CreateConnector().ExtractAsync(new ExtractRequest(profile), writer, Ct);
            return await writer.CompleteAsync(Ct);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class ParquetConnectorTests : ConnectorContractTests
{
    private readonly FakeSourceFileAccessor _files = new();

    protected override DataSourceKind ExpectedKind => DataSourceKind.Parquet;

    protected override string ExpectedTableId => FileConnectorBase.FileTableId;

    protected override long ExpectedRowCount => RetailFiles.Rows;

    protected override IReadOnlyCollection<string> ExpectedColumns => ["order_id", "category", "quantity", "cost"];

    protected override IDataSourceConnector CreateConnector() => new ParquetConnector(_files);

    protected override async Task<ConnectionProfile> CreateValidProfileAsync()
    {
        var (_, parquet) = await RetailFiles.GetAsync();
        return ConnectionProfile.ForStoredFile(RetailModel.Tenant, DataSourceKind.Parquet, _files.Add(parquet), "retail_sales.parquet");
    }

    protected override ConnectionProfile CreateUnreachableProfile() =>
        ConnectionProfile.ForStoredFile(RetailModel.Tenant, DataSourceKind.Parquet, Guid.NewGuid(), "missing.parquet");

    [Fact]
    public async Task Extract_NotAParquetFile_ThrowsConnectorException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"insightflow-bad-{Guid.NewGuid():N}.parquet");
        await File.WriteAllTextAsync(path, "this is not parquet", Ct);
        try
        {
            var profile = ConnectionProfile.ForStoredFile(RetailModel.Tenant, DataSourceKind.Parquet, _files.Add(path), "bad.parquet");
            await using var writer = await Extraction.DuckDbExtractWriter.CreateAsync(NewWorkDirectory(), "256MB", Ct);

            await Should.ThrowAsync<ConnectorException>(() => CreateConnector().ExtractAsync(new ExtractRequest(profile), writer, Ct));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
