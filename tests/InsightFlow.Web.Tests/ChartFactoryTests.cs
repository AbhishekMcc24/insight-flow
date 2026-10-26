using System.Text.Json.Nodes;
using InsightFlow.Contracts.Query;
using InsightFlow.Web.Charts;

namespace InsightFlow.Web.Tests;

/// <summary>Every chart type draws from the rows of an answer; a named type is honoured.</summary>
public sealed class ChartFactoryTests
{
    private static QueryResult Table((string Name, ColumnType Type)[] columns, params object?[][] rows) =>
        new([.. columns.Select(c => new QueryColumn(c.Name, c.Type))], [.. rows.Select(r => (IReadOnlyList<object?>)r)], false);

    private static QueryResult Regions() => Table(
        [("region", ColumnType.String), ("sales", ColumnType.Number), ("cost", ColumnType.Number), ("units", ColumnType.Integer)],
        ["North", 120.5, 80.0, 40], ["South", 90.0, 60.0, 30], ["East", 150.0, 95.0, 55], ["West", 60.0, 40.0, 20]);

    private static QueryResult Daily() => Table(
        [("day", ColumnType.Date), ("sales", ColumnType.Number)],
        ["2026-01-01", 10.0], ["2026-01-02", 14.0], ["2026-01-03", 9.0], ["2026-01-04", 21.0]);

    private static JsonObject Option(QueryResult result, string type, string? x = null, string[]? y = null, string? series = null) =>
        JsonNode.Parse(ChartFactory.Build(result, new ChartSpecDto(type, x, y, series), dark: false).ShouldNotBeNull())!.AsObject();

    private static string FirstSeriesType(JsonObject option) => option["series"]!.AsArray()[0]!["type"]!.GetValue<string>();

    [Theory]
    [InlineData("bar", "bar")]
    [InlineData("hbar", "bar")]
    [InlineData("stackedbar", "bar")]
    [InlineData("line", "line")]
    [InlineData("area", "line")]
    [InlineData("pie", "pie")]
    [InlineData("donut", "pie")]
    [InlineData("rose", "pie")]
    [InlineData("funnel", "funnel")]
    [InlineData("treemap", "treemap")]
    [InlineData("sunburst", "sunburst")]
    [InlineData("scatter", "scatter")]
    [InlineData("radar", "radar")]
    [InlineData("combo", "bar")]
    [InlineData("polar", "bar")]
    [InlineData("waterfall", "bar")]
    [InlineData("gauge", "gauge")]
    public void Build_NamedType_DrawsThatSeriesType(string type, string expected) =>
        FirstSeriesType(Option(Regions(), type)).ShouldBe(expected);

    [Fact]
    public void Build_Pie_UsesTheCategoryAndTheFirstMeasure()
    {
        var data = Option(Regions(), "pie")["series"]![0]!["data"]!.AsArray();

        data.Count.ShouldBe(4);
        data.Select(d => d!["name"]!.GetValue<string>()).ShouldBe(["East", "North", "South", "West"]);
        data[0]!["value"]!.GetValue<double>().ShouldBe(150.0);
    }

    [Fact]
    public void Build_Donut_HasAnInnerRadius() =>
        Option(Regions(), "donut")["series"]![0]!["radius"]!.AsArray()[0]!.GetValue<string>().ShouldBe("45%");

    [Fact]
    public void Build_PieWithManyCategories_GroupsTheSmallOnesAsOther()
    {
        var rows = Enumerable.Range(1, 20).Select(i => (object?[])[$"item {i}", (double)i]).ToArray();
        var result = Table([("item", ColumnType.String), ("amount", ColumnType.Number)], rows);

        var data = Option(result, "pie")["series"]![0]!["data"]!.AsArray();

        data.Count.ShouldBe(ChartFactory.MaxSlices);
        data[^1]!["name"]!.GetValue<string>().ShouldBe("Other");
    }

    [Fact]
    public void Build_HorizontalBar_SwapsTheAxes()
    {
        var option = Option(Regions(), "hbar");

        option["yAxis"]!["type"]!.GetValue<string>().ShouldBe("category");
        option["xAxis"]!["type"]!.GetValue<string>().ShouldBe("value");
    }

    [Fact]
    public void Build_StackedBar_StacksEveryMeasure()
    {
        var series = Option(Regions(), "stackedbar", y: ["sales", "cost"])["series"]!.AsArray();

        series.Count.ShouldBe(2);
        series.Select(s => s!["stack"]!.GetValue<string>()).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void Build_Scatter_PlotsTwoMeasuresAgainstEachOther()
    {
        var option = Option(Regions(), "scatter");

        option["xAxis"]!["name"]!.GetValue<string>().ShouldBe("sales");
        option["yAxis"]!["name"]!.GetValue<string>().ShouldBe("cost");
        option["series"]![0]!["data"]!.AsArray().Count.ShouldBe(4);
    }

    [Fact]
    public void Build_Bubble_SizesPointsByTheThirdMeasure()
    {
        var points = Option(Regions(), "bubble")["series"]![0]!["data"]!.AsArray();

        points.Select(p => p!["symbolSize"]!.GetValue<double>()).Distinct().Count().ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Build_Line_ForDatesIsSmoothed() =>
        Option(Daily(), "line")["series"]![0]!["smooth"]!.GetValue<bool>().ShouldBeTrue();

    [Fact]
    public void Build_Waterfall_AddsATotalAndAnInvisibleBase()
    {
        var option = Option(Regions(), "waterfall");
        var categories = option["xAxis"]!["data"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();

        categories[^1].ShouldBe("Total");
        option["series"]!.AsArray().Count.ShouldBe(4);
    }

    [Fact]
    public void Build_Sankey_DrawsEveryFlow()
    {
        var flows = Table(
            [("from", ColumnType.String), ("to", ColumnType.String), ("amount", ColumnType.Number)],
            ["Plant", "Depot", 100.0], ["Depot", "Shop", 70.0], ["Depot", "Shop 2", 30.0]);

        Option(flows, "sankey")["series"]![0]!["links"]!.AsArray().Count.ShouldBe(3);
    }

    [Fact]
    public void Build_SankeyWithACycle_DropsTheEdgeThatClosesIt()
    {
        var flows = Table(
            [("from", ColumnType.String), ("to", ColumnType.String), ("amount", ColumnType.Number)],
            ["A", "B", 10.0], ["B", "C", 8.0], ["C", "A", 5.0]);

        Option(flows, "sankey")["series"]![0]!["links"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Build_Network_UsesTheSameFlows()
    {
        var flows = Table(
            [("from", ColumnType.String), ("to", ColumnType.String), ("amount", ColumnType.Number)],
            ["A", "B", 10.0], ["B", "C", 8.0], ["C", "A", 5.0]);

        Option(flows, "graph")["series"]![0]!["links"]!.AsArray().Count.ShouldBe(3);
    }

    [Fact]
    public void Build_Heatmap_PivotsTwoCategoriesAndAMeasure()
    {
        var grid = Table(
            [("region", ColumnType.String), ("month", ColumnType.String), ("sales", ColumnType.Number)],
            ["North", "Jan", 10.0], ["North", "Feb", 12.0], ["South", "Jan", 7.0], ["South", "Feb", 9.0]);

        Option(grid, "heatmap")["series"]![0]!["data"]!.AsArray().Count.ShouldBe(4);
    }

    [Fact]
    public void Build_Candlestick_FindsOpenHighLowCloseByName()
    {
        var prices = Table(
            [("day", ColumnType.Date), ("close", ColumnType.Number), ("open", ColumnType.Number), ("high", ColumnType.Number), ("low", ColumnType.Number)],
            ["2026-01-01", 11.0, 10.0, 12.0, 9.0], ["2026-01-02", 12.0, 11.0, 13.0, 10.5]);

        var first = Option(prices, "candlestick")["series"]![0]!["data"]![0]!.AsArray();

        first.Select(v => v!.GetValue<double>()).ShouldBe([10.0, 11.0, 9.0, 12.0]); // open, close, low, high
    }

    [Fact]
    public void Build_BoxPlot_ComputesTheFiveNumberSummary()
    {
        var values = Table([("amount", ColumnType.Number)], [1.0], [2.0], [3.0], [4.0], [5.0]);

        var summary = Option(values, "boxplot")["series"]![0]!["data"]![0]!.AsArray().Select(v => v!.GetValue<double>()).ToList();

        summary.ShouldBe([1.0, 2.0, 3.0, 4.0, 5.0]);
    }

    [Fact]
    public void Quantile_InterpolatesBetweenValues() => ChartFactory.Quantile([1, 2, 3, 4], 0.5).ShouldBe(2.5);

    [Fact]
    public void Build_UnknownColumnsInTheSpec_FallBackToTheShapeOfTheRows() =>
        Option(Regions(), "pie", x: "no_such_column", y: ["nor_this"])["series"]![0]!["data"]!.AsArray().Count.ShouldBe(4);

    [Fact]
    public void Build_SeriesColumn_SplitsABarChartIntoSeries()
    {
        var grid = Table(
            [("region", ColumnType.String), ("month", ColumnType.String), ("sales", ColumnType.Number)],
            ["North", "Jan", 10.0], ["North", "Feb", 12.0], ["South", "Jan", 7.0], ["South", "Feb", 9.0]);

        var series = Option(grid, "bar", x: "month", y: ["sales"], series: "region")["series"]!.AsArray();

        series.Select(s => s!["name"]!.GetValue<string>()).ShouldBe(["North", "South"]);
    }

    [Fact]
    public void Compatible_OffersOnlyTypesThatFitTheRows()
    {
        var types = ChartFactory.Compatible(Regions()).Select(t => t.Key).ToList();

        types.ShouldContain("pie");
        types.ShouldContain("scatter");
        types.ShouldNotContain("sankey");      // needs two category columns
        types.ShouldNotContain("candlestick"); // needs four price columns
        types.ShouldNotContain("calendar");    // needs a date
    }

    [Fact]
    public void Compatible_ADateColumn_OffersTrendCharts()
    {
        var types = ChartFactory.Compatible(Daily()).Select(t => t.Key).ToList();

        types.ShouldContain("line");
        types.ShouldContain("calendar");
    }

    [Fact]
    public void Choose_RequestedTypeThatFits_WinsOverTheAutomaticOne()
    {
        ChartFactory.Choose(Regions(), "pie").ShouldBe("pie");
        ChartFactory.Choose(Regions(), null).ShouldBe("bar");
        ChartFactory.Choose(Daily(), null).ShouldBe("line");
    }

    [Fact]
    public void Choose_RequestedTypeThatCannotDrawTheRows_FallsBackInsteadOfFailing() =>
        ChartFactory.Choose(Regions(), "sankey").ShouldBe("bar");

    [Fact]
    public void Build_ASingleNumber_IsOnlyAGauge()
    {
        var one = Table([("total", ColumnType.Number)], [4200.0]);

        ChartFactory.Compatible(one).Select(t => t.Key).ShouldBe(["gauge"]);
        ChartFactory.CanChart(one).ShouldBeFalse();
    }

    [Fact]
    public void Build_TextOnly_DrawsNothing() =>
        ChartFactory.Build(Table([("name", ColumnType.String)], ["a"], ["b"]), new ChartSpecDto("pie"), false).ShouldBeNull();

    [Theory]
    [InlineData("show total outstanding as a pie chart", "pie")]
    [InlineData("give me a donut chart of sales by region", "donut")]
    [InlineData("sales by region as a doughnut", "donut")]
    [InlineData("a stacked bar chart of sales by month", "stackedbar")]
    [InlineData("show the customer flow as a sankey diagram", "sankey")]
    [InlineData("plot a scatter plot of cost versus sales", "scatter")]
    [InlineData("heat map of sales by region and month", "heatmap")]
    [InlineData("funnel of the sales stages", "funnel")]
    [InlineData("a radar chart of the scores", "radar")]
    [InlineData("show a line graph of sales", "line")]
    [InlineData("horizontal bar chart of sales", "hbar")]
    [InlineData("treemap of the invoices", "treemap")]
    [InlineData("box plot of invoice amounts", "boxplot")]
    [InlineData("what is the total outstanding amount", null)]
    [InlineData("show me sales by region", null)]
    public void FromQuestion_FindsTheChartAPersonAskedFor(string question, string? expected) =>
        ChartTypes.FromQuestion(question).ShouldBe(expected);

    [Theory]
    [InlineData("Pie", "pie")]
    [InlineData("doughnut", "donut")]
    [InlineData("Column", "bar")]
    [InlineData("stacked column", "stackedbar")]
    [InlineData("scatter plot", "scatter")]
    [InlineData("nonsense", null)]
    public void Normalize_MapsNamesToChartKeys(string name, string? expected) => ChartTypes.Normalize(name).ShouldBe(expected);

    [Fact]
    public void AllTypes_AreUniqueAndKnown()
    {
        ChartTypes.All.Select(t => t.Key).Distinct().Count().ShouldBe(ChartTypes.All.Count);
        ChartTypes.All.ShouldAllBe(t => ChartTypes.IsKnown(t.Key));
    }
}
