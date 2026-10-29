using InsightFlow.Domain.Documents;
using InsightFlow.Domain.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InsightFlow.Tests.Infrastructure.Connectors;

/// <summary>Reading Excel workbooks with DuckDB: what parses, and how problems are reported for the person to resolve.</summary>
public sealed class SpreadsheetReaderTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.New();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "insightflow-xlsx-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingUploader _uploader = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SpreadsheetReader Reader() =>
        new(_uploader, Options.Create(new ConnectorOptions { WorkDirectory = Path.Combine(_dir, "work") }), TimeProvider.System, NullLogger<SpreadsheetReader>.Instance);

    private string Workbook(string name, params (string Name, IReadOnlyList<object?[]> Rows)[] sheets) => XlsxBuilder.Write(_dir, name, sheets);

    private static List<object?[]> NumbersWithATextCellAtTheEnd()
    {
        var rows = new List<object?[]> { new object?[] { "id", "amount" } };
        rows.AddRange(Enumerable.Range(1, 3_000).Select(i => new object?[] { i, i * 1.5 }));
        rows.Add([3_001, "N/A"]);
        rows.Add([3_002, 7.5]);
        return rows;
    }

    [Fact]
    public async Task ParseSheet_CleanSheet_BecomesATypedTable_WithNormalisedColumnNames()
    {
        var path = Workbook("clean.xlsx", ("Sales", [["Order ID", "Region Name", "Amount"], [1, "North", 10.5], [2, "South", 20.0]]));

        var outcome = await Reader().ParseSheetAsync(Tenant, path, "Sales", new SheetSettings(), "tester", Ct);

        var parsed = outcome.ShouldBeOfType<SheetParsed>();
        parsed.Rows.ShouldBe(2);
        parsed.Columns.ShouldBe(3);
        parsed.Version.Schema.Columns.Select(c => c.Name).ShouldBe(["order_id", "region_name", "amount"]);
        _uploader.Uploaded.ShouldContain(parsed.Version.Id);
    }

    [Fact]
    public async Task ParseSheet_ATextCellInANumberColumn_IsReportedWithTheCellAndTheChoices()
    {
        var path = Workbook("mixed.xlsx", ("Data", NumbersWithATextCellAtTheEnd()));

        var outcome = await Reader().ParseSheetAsync(Tenant, path, "Data", new SheetSettings(), "tester", Ct);

        var failed = outcome.ShouldBeOfType<SheetFailed>();
        failed.Kind.ShouldBe(SpreadsheetProblems.TypeConflict);
        failed.Message.ShouldContain("B3002");
        failed.Message.ShouldContain("N/A");
        failed.Message.ShouldContain("a number");
        failed.Technical.ShouldNotBeNull().ShouldContain("Could not convert");
        failed.Options.Select(o => o.Key).ShouldBe([ProblemChoices.AsText, ProblemChoices.IgnoreErrors, ProblemChoices.SkipSheet]);
        _uploader.Uploaded.ShouldBeEmpty(); // nothing half-read is saved
    }

    [Fact]
    public async Task ParseSheet_AfterChoosingText_ReadsEverythingAsText()
    {
        var path = Workbook("mixed.xlsx", ("Data", NumbersWithATextCellAtTheEnd()));

        var outcome = await Reader().ParseSheetAsync(Tenant, path, "Data", new SheetSettings(AllVarchar: true), "tester", Ct);

        outcome.ShouldBeOfType<SheetParsed>().Rows.ShouldBe(3_002);
    }

    [Fact]
    public async Task ParseSheet_AfterChoosingToLeaveBadCellsEmpty_KeepsTheNumbers()
    {
        var path = Workbook("mixed.xlsx", ("Data", NumbersWithATextCellAtTheEnd()));

        var outcome = await Reader().ParseSheetAsync(Tenant, path, "Data", new SheetSettings(IgnoreErrors: true), "tester", Ct);

        var parsed = outcome.ShouldBeOfType<SheetParsed>();
        parsed.Rows.ShouldBe(3_002);
        parsed.Version.Schema.Columns.Single(c => c.Name == "amount").DataType.ShouldBe(InsightFlow.Domain.Modeling.DataType.Decimal);
    }

    [Fact]
    public async Task ParseSheet_TableBelowATitle_IsFoundAutomatically_AndAGivenRangeStillWorks()
    {
        var path = Workbook(
            "report.xlsx",
            ("Report", [["Quarterly report", null, null], [null, null, null], ["id", "name", "total"], [1, "a", 5.5], [2, "b", 6.5]]));

        var first = await Reader().ParseSheetAsync(Tenant, path, "Report", new SheetSettings(), "tester", Ct);
        var second = await Reader().ParseSheetAsync(Tenant, path, "Report", new SheetSettings(Range: "A3:C5"), "tester", Ct);

        var found = first.ShouldBeOfType<SheetParsed>();
        found.Rows.ShouldBe(2);
        found.Version.Schema.Columns.Select(c => c.Name).ShouldBe(["id", "name", "total"]);
        second.ShouldBeOfType<SheetParsed>().Rows.ShouldBe(2);
    }

    [Fact]
    public async Task ParseSheet_BlankSheet_IsEmpty_NotAProblem()
    {
        var path = Workbook("blank.xlsx", ("Sales", [["a"], [1]]), ("Blank", []));

        var outcome = await Reader().ParseSheetAsync(Tenant, path, "Blank", new SheetSettings(), "tester", Ct);

        outcome.ShouldBeOfType<SheetEmpty>();
    }

    [Fact]
    public void ListSheets_ReturnsEverySheetInOrder()
    {
        var path = Workbook("multi.xlsx", ("Sales", [["a"], [1]]), ("Q1 & Q2", [["a"], [1]]), ("Blank", []));

        SpreadsheetReader.ListSheets(path).ShouldBe(["Sales", "Q1 & Q2", "Blank"]);
    }

    [Fact]
    public async Task ListSheets_ANotAWorkbook_Throws_AndParseReportsItAsUnreadable()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "broken.xlsx");
        await File.WriteAllTextAsync(path, "this is not a zip", Ct);

        Should.Throw<SpreadsheetException>(() => SpreadsheetReader.ListSheets(path));
        var outcome = await Reader().ParseSheetAsync(Tenant, path, "Sheet1", new SheetSettings(), "tester", Ct);
        outcome.ShouldBeOfType<SheetFailed>().Kind.ShouldBe(SpreadsheetProblems.Unreadable);
    }

    [Theory]
    [InlineData("Invalid Input Error: read_xlsx: Failed to parse cell 'C12': Could not convert string 'abc' to DOUBLE", SpreadsheetProblems.TypeConflict)]
    [InlineData("IO Error: Failed to open zip for reading", SpreadsheetProblems.Unreadable)]
    [InlineData("Binder Error: Sheet \"Nope\" not found in xlsx file \"x.xlsx\"", SpreadsheetProblems.ParseError)]
    [InlineData("Something nobody expected", SpreadsheetProblems.ParseError)]
    public void Classify_TurnsDuckDbMessagesIntoProblems(string message, string kind)
    {
        var outcome = SpreadsheetProblems.Classify(new SpreadsheetException(message), "Sheet1", new SheetSettings());

        var failed = outcome.ShouldBeOfType<SheetFailed>();
        failed.Kind.ShouldBe(kind);
        failed.Options.ShouldNotBeEmpty();
        failed.Technical.ShouldBe(message);
    }

    [Fact]
    public void Classify_EngineUnavailable_OffersARetry()
    {
        var outcome = SpreadsheetProblems.Classify(new SpreadsheetException("HTTP Error: could not download", SpreadsheetFailure.EngineUnavailable), "Sheet1", new SheetSettings());

        outcome.ShouldBeOfType<SheetFailed>().Options.Select(o => o.Key).ShouldBe([ProblemChoices.Retry, ProblemChoices.SkipFile]);
    }

    [Fact]
    public void Classify_UnknownError_NeverOffersAChoiceAlreadyTried()
    {
        var tried = new SheetSettings(Header: false, Range: "A1:B2", AllVarchar: true);

        var failed = SpreadsheetProblems.Classify(new SpreadsheetException("odd"), "S", tried).ShouldBeOfType<SheetFailed>();

        failed.Options.Select(o => o.Key).ShouldBe([ProblemChoices.SkipSheet]);
    }

    [Fact]
    public void TableName_IsASafeUniqueIdentifier()
    {
        var used = new HashSet<string>();

        SpreadsheetReader.TableName("Q3 Sales (final).xlsx", "Region #1", used).ShouldBe("q3_sales_final_region_1");
        SpreadsheetReader.TableName("Q3 Sales (final).xlsx", "Region #1", used).ShouldBe("q3_sales_final_region_1_2");
        SpreadsheetReader.TableName("2024.xlsx", "Data", used).ShouldBe("t_2024_data");
        SpreadsheetReader.TableName("###.xlsx", "***", used).ShouldBe("sheet");
    }

    [Fact]
    public void Describe_SaysHowASheetWasRead()
    {
        new SheetSettings().Describe().ShouldBeNull();
        new SheetSettings(AllVarchar: true, Range: "A3:C9").Describe().ShouldBe("every column read as text, range A3:C9");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp folder; the next run of the machine's cleanup removes it.
        }
    }

    private sealed class RecordingUploader : IExtractUploader
    {
        public List<Guid> Uploaded { get; } = [];

        public Task UploadAsync(TenantId tenant, Guid datasetVersionId, string localParquetPath, CancellationToken cancellationToken)
        {
            File.Exists(localParquetPath).ShouldBeTrue();
            Uploaded.Add(datasetVersionId);
            return Task.CompletedTask;
        }
    }
}
