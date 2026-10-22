using InsightFlow.Connectors.Files;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Modeling;
using Microsoft.Extensions.Options;

namespace InsightFlow.Connectors.Tests;

public sealed class ExcelConnectorTests : ConnectorContractTests, IDisposable
{
    private const int Rows = 250;
    private readonly FakeSourceFileAccessor _files = new();
    private readonly Lazy<string> _workbook = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"insightflow-xlsx-{Guid.NewGuid():N}.xlsx");
        ExcelWorkbook.WriteOrders(path, Rows);
        return path;
    });

    protected override DataSourceKind ExpectedKind => DataSourceKind.Excel;

    protected override string ExpectedTableId => "Orders";

    protected override long ExpectedRowCount => Rows;

    protected override IReadOnlyCollection<string> ExpectedColumns => ["id", "region", "amount", "ordered_on"];

    protected override IDataSourceConnector CreateConnector() =>
        new ExcelConnector(_files, Options.Create(new ConnectorOptions()));

    protected override Task<ConnectionProfile> CreateValidProfileAsync() =>
        Task.FromResult(ConnectionProfile.ForStoredFile(InsightFlow.Testing.RetailModel.Tenant, DataSourceKind.Excel, _files.Add(_workbook.Value), "orders.xlsx"));

    protected override ConnectionProfile CreateUnreachableProfile() =>
        ConnectionProfile.ForStoredFile(InsightFlow.Testing.RetailModel.Tenant, DataSourceKind.Excel, Guid.NewGuid(), "missing.xlsx");

    [Fact]
    public async Task Discover_Workbook_ListsEachWorksheet()
    {
        var tables = new List<SourceTable>();
        await foreach (var table in CreateConnector().DiscoverAsync(await CreateValidProfileAsync(), Ct))
        {
            tables.Add(table);
        }

        tables.Select(t => t.Id).ShouldBe(["Orders", "Notes"]);
    }

    [Fact]
    public async Task Extract_Orders_InfersLogicalTypes()
    {
        await using var writer = await Extraction.DuckDbExtractWriter.CreateAsync(NewWorkDirectory(), "256MB", Ct);
        await CreateConnector().ExtractAsync(new ExtractRequest(await CreateValidProfileAsync(), "Orders"), writer, Ct);
        var output = await writer.CompleteAsync(Ct);
        var types = output.Schema.Columns.ToDictionary(c => c.Name, c => c.DataType);

        types["id"].ShouldBe(DataType.Integer);
        types["region"].ShouldBe(DataType.String);
        types["amount"].ShouldBe(DataType.Decimal);
        types["ordered_on"].ShouldBeOneOf(DataType.Date, DataType.DateTime);
    }

    [Fact]
    public async Task Extract_NullTable_ReadsTheFirstWorksheet()
    {
        await using var writer = await Extraction.DuckDbExtractWriter.CreateAsync(NewWorkDirectory(), "256MB", Ct);
        await CreateConnector().ExtractAsync(new ExtractRequest(await CreateValidProfileAsync()), writer, Ct);
        var output = await writer.CompleteAsync(Ct);

        output.RowCount.ShouldBe(Rows);
        output.Schema.Columns.Select(c => c.Name).ShouldContain("amount");
    }

    [Fact]
    public void Classify_MapsClrValues()
    {
        ExcelConnector.Classify(12d).ShouldBe(DataType.Integer);
        ExcelConnector.Classify(1.25d).ShouldBe(DataType.Decimal);
        ExcelConnector.Classify(true).ShouldBe(DataType.Boolean);
        ExcelConnector.Classify(new DateTime(2025, 1, 1)).ShouldBe(DataType.Date);
        ExcelConnector.Classify(new DateTime(2025, 1, 1, 8, 0, 0)).ShouldBe(DataType.DateTime);
        ExcelConnector.Classify("North").ShouldBe(DataType.String);
    }

    public void Dispose()
    {
        if (_workbook.IsValueCreated)
        {
            File.Delete(_workbook.Value);
        }
    }
}
