using InsightFlow.Domain.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InsightFlow.Connectors.Tests;

/// <summary>Sheets with a title row and column names spread over two rows (a group row above the names), like many exported reports.</summary>
public sealed class SheetLayoutTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.New();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "insightflow-layout-tests-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<object?[]> Report()
    {
        var rows = new List<object?[]>
        {
            new object?[] { "Financial Year 2627", "Report As On Date - 14/10/2026", null, null, null, null },
            new object?[] { "Network Expansion Format", null, "Dealer Mapping", null, "Spend", null },
            new object?[] { "Region", "State", "Dealer", "Store", "Dealer", "Store" },
        };
        rows.AddRange(Enumerable.Range(1, 40).Select(i => new object?[] { "C1", "Delhi", i, i * 2, i * 3.5, i * 4 }));
        return rows;
    }

    [Fact]
    public void Detect_TitleGroupRowAndNamesRow_FindsTheTableAndBuildsUniqueNames()
    {
        var preview = Report().Take(SheetLayoutDetector.PreviewRows).Select(r => Pad(r, 6)).ToList();

        var layout = SheetLayoutDetector.Detect(preview).ShouldNotBeNull();

        layout.FirstDataRow.ShouldBe(4);
        layout.HeaderRows.ShouldBe(2);
        layout.ColumnNames.ShouldBe(["region", "state", "dealer_mapping_dealer", "dealer_mapping_store", "spend_dealer", "spend_store"]);
    }

    [Fact]
    public void Detect_OrdinarySheet_ReturnsNull()
    {
        var rows = new List<string?[]> { new[] { "id", "name", "amount" } };
        rows.AddRange(Enumerable.Range(1, 10).Select(i => new string?[] { i.ToString(System.Globalization.CultureInfo.InvariantCulture), "x", "1.5" }));

        SheetLayoutDetector.Detect(rows).ShouldBeNull();
    }

    [Fact]
    public async Task ParseSheet_ReportWithTwoHeaderRows_ReadsTheDataWithTypedNamedColumns()
    {
        var path = XlsxBuilder.Write(_dir, "report.xlsx", ("Sheet1", Report()));
        var reader = new SpreadsheetReader(
            new NoopUploader(), Options.Create(new ConnectorOptions { WorkDirectory = Path.Combine(_dir, "work") }), TimeProvider.System, NullLogger<SpreadsheetReader>.Instance);

        var outcome = await reader.ParseSheetAsync(Tenant, path, "Sheet1", new SheetSettings(), "tester", Ct);

        var failed = outcome as SheetFailed;
        (failed?.Technical ?? failed?.Message).ShouldBeNull();
        var parsed = outcome.ShouldBeOfType<SheetParsed>();
        parsed.Rows.ShouldBe(40);
        parsed.Version.Schema.Columns.Select(c => c.Name).ShouldBe(["region", "state", "dealer_mapping_dealer", "dealer_mapping_store", "spend_dealer", "spend_store"]);
        parsed.Version.Schema.Columns[2].DataType.ShouldNotBe(InsightFlow.Domain.Modeling.DataType.String);
        parsed.Layout.ShouldNotBeNull().FirstDataRow.ShouldBe(4);
    }

    [Fact]
    public async Task ParseSheet_SubtotalRows_AreFlaggedSoSumsDoNotCountThemTwice()
    {
        var rows = Report();
        rows.Add(["C1 Region Total", null, null, 820, 2870.0, 3280]);
        rows.Add(["Grand Total", null, null, 820, 2870.0, 3280]);
        var path = XlsxBuilder.Write(_dir, "totals.xlsx", ("Sheet1", rows));
        var reader = new SpreadsheetReader(
            new NoopUploader(), Options.Create(new ConnectorOptions { WorkDirectory = Path.Combine(_dir, "work") }), TimeProvider.System, NullLogger<SpreadsheetReader>.Instance);

        var parsed = (await reader.ParseSheetAsync(Tenant, path, "Sheet1", new SheetSettings(), "tester", Ct)).ShouldBeOfType<SheetParsed>();

        parsed.TotalRows.ShouldBe(2);
        parsed.Rows.ShouldBe(42);
        parsed.Version.Schema.Columns.Select(c => c.Name).ShouldContain("is_total_row");
    }

    private sealed class NoopUploader : Extraction.IExtractUploader
    {
        public Task UploadAsync(TenantId tenant, Guid datasetVersionId, string localParquetPath, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static string?[] Pad(object?[] row, int width) =>
        [.. Enumerable.Range(0, width).Select(i => i < row.Length ? Convert.ToString(row[i], System.Globalization.CultureInfo.InvariantCulture) : null)];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Scratch space.
        }
    }
}
