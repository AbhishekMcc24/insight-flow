using System.Text.Json;
using System.Text.Json.Nodes;
using InsightFlow.Contracts;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Viz;
using InsightFlow.Testing;
using InsightFlow.Web.Charts;
using SortDirection = InsightFlow.Domain.Viz.SortDirection;

namespace InsightFlow.Web.Tests;

/// <summary>
/// VizSpec + QueryResult → ECharts option golden files (Golden/*.verified.json) plus behavioural checks. Results are
/// shaped exactly like the query engine returns them: one column per channel, named after the channel, and round-tripped
/// through the contracts JSON context so cells are <see cref="JsonElement"/>s, as in the Web app.
/// </summary>
public sealed class EChartsOptionBuilderTests
{
    private static readonly Guid Dataset = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // ---------------------------------------------------------------------------------------------
    // Golden files (one per mark)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Bar_StackedByColor_MatchesGolden()
    {
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("category"), new FieldRef("revenue", Agg.Sum), new FieldRef("channel")),
            sort: [new SortSpec("revenue", SortDirection.Desc)]);
        var result = Result(
            [Column("x", ColumnType.String, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y), Column("color", ColumnType.String, spec.Encoding.Color)],
            ["Toys", 500.0, "Store"], ["Books", 450.0, "Store"], ["Books", 300.0, "Online"], ["Toys", 100.0, "Online"]);

        Golden.MatchJson(EChartsOptionBuilder.BuildJson(spec, result), "bar_category_revenue_by_channel");
    }

    [Fact]
    public void Line_MonthlyByColor_MatchesGolden()
    {
        var spec = Spec(Mark.Line, new VizEncoding(
            new FieldRef("order_date", TimeUnit: TimeUnit.Month), new FieldRef("orders.quantity", Agg.Count), new FieldRef("channel")));
        var result = Result(
            [Column("x", ColumnType.Date, spec.Encoding.X), Column("y", ColumnType.Integer, spec.Encoding.Y), Column("color", ColumnType.String, spec.Encoding.Color)],
            ["2025-01-01", 12L, "Online"], ["2025-01-01", 7L, "Store"], ["2025-02-01", 15L, "Online"]);

        Golden.MatchJson(EChartsOptionBuilder.BuildJson(spec, result), "line_month_count_by_channel");
    }

    [Fact]
    public void Area_Quarterly_MatchesGolden()
    {
        var spec = Spec(Mark.Area, new VizEncoding(new FieldRef("order_date", TimeUnit: TimeUnit.Quarter), new FieldRef("quantity", Agg.Sum)));
        var result = Result(
            [Column("x", ColumnType.Date, spec.Encoding.X), Column("y", ColumnType.Integer, spec.Encoding.Y)],
            ["2025-01-01", 120L], ["2025-04-01", 140L], ["2025-07-01", 90L]);

        Golden.MatchJson(EChartsOptionBuilder.BuildJson(spec, result), "area_quarter_quantity");
    }

    [Fact]
    public void Pie_ValueByCategory_MatchesGolden()
    {
        var spec = Spec(Mark.Pie, new VizEncoding(null, new FieldRef("revenue", Agg.Sum), new FieldRef("category")));
        var result = Result(
            [Column("y", ColumnType.Number, spec.Encoding.Y), Column("color", ColumnType.String, spec.Encoding.Color)],
            [500.0, "Toys"], [300.0, "Books"]);

        Golden.MatchJson(EChartsOptionBuilder.BuildJson(spec, result), "pie_revenue_by_category");
    }

    [Fact]
    public void Heatmap_TwoCategoriesByMeasure_MatchesGolden()
    {
        var spec = Spec(Mark.Heatmap, new VizEncoding(new FieldRef("region"), new FieldRef("channel"), new FieldRef("revenue", Agg.Avg)));
        var result = Result(
            [Column("x", ColumnType.String, spec.Encoding.X), Column("y", ColumnType.String, spec.Encoding.Y), Column("color", ColumnType.Number, spec.Encoding.Color)],
            ["West", "Online", 10.5], ["West", "Store", 8.0], ["East", "Online", 12.25]);

        Golden.MatchJson(EChartsOptionBuilder.BuildJson(spec, result), "heatmap_region_channel_avg_revenue");
    }

    [Fact]
    public void Point_Scatter_MatchesGolden()
    {
        var spec = Spec(Mark.Point, new VizEncoding(new FieldRef("quantity"), new FieldRef("revenue")));
        var result = Result([Column("x", ColumnType.Integer, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y)], [1L, 2.5], [3L, 7.25]);

        Golden.MatchJson(EChartsOptionBuilder.BuildJson(spec, result), "point_quantity_revenue");
    }

    // ---------------------------------------------------------------------------------------------
    // Behaviour
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Bar_StackedAndSorted_OrdersCategoriesByTotal()
    {
        // Rows arrive ordered by segment value; the stacked chart must order bars by their totals (Books 750 > Toys 600).
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("category"), new FieldRef("revenue", Agg.Sum), new FieldRef("channel")),
            sort: [new SortSpec("revenue", SortDirection.Desc)]);
        var result = Result(
            [Column("x", ColumnType.String, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y), Column("color", ColumnType.String, spec.Encoding.Color)],
            ["Toys", 500.0, "Store"], ["Books", 450.0, "Store"], ["Books", 300.0, "Online"], ["Toys", 100.0, "Online"]);

        var option = EChartsOptionBuilder.Build(spec, result);

        Strings(option["xAxis"]!["data"]!).ShouldBe(["Books", "Toys"]);
        option["series"]!.AsArray().Select(s => s!["stack"]!.GetValue<string>()).ShouldAllBe(s => s == "total");
        option["series"]![0]!["data"]!.ToJsonString().ShouldBe("[450,500]");
        option["series"]![1]!["data"]!.ToJsonString().ShouldBe("[300,100]");
    }

    [Fact]
    public void Bar_NumericXCategoricalY_IsHorizontal()
    {
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("revenue", Agg.Sum), new FieldRef("region")));
        var result = Result([Column("x", ColumnType.Number, spec.Encoding.X), Column("y", ColumnType.String, spec.Encoding.Y)], [10.0, "West"], [5.0, "East"]);

        var option = EChartsOptionBuilder.Build(spec, result);

        option["yAxis"]!["type"]!.GetValue<string>().ShouldBe("category");
        Strings(option["yAxis"]!["data"]!).ShouldBe(["West", "East"]);
        option["xAxis"]!["type"]!.GetValue<string>().ShouldBe("value");
    }

    [Fact]
    public void Pie_SlicesFollowRowOrder()
    {
        var spec = Spec(Mark.Pie, new VizEncoding(null, new FieldRef("revenue", Agg.Sum), new FieldRef("category")));
        var result = Result([Column("y", ColumnType.Number, spec.Encoding.Y), Column("color", ColumnType.String, spec.Encoding.Color)],
            [900.0, "Toys"], [500.0, "Books"], [100.0, "Art"]);

        var slices = EChartsOptionBuilder.Build(spec, result)["series"]![0]!["data"]!.AsArray();

        slices.Select(s => s!["name"]!.GetValue<string>()).ShouldBe(["Toys", "Books", "Art"]);
    }

    [Fact]
    public void Line_RawDates_UseTimeAxisWithZoom()
    {
        var spec = Spec(Mark.Line, new VizEncoding(new FieldRef("order_date"), new FieldRef("revenue", Agg.Sum)));
        var result = Result([Column("x", ColumnType.Date, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y)],
            ["2025-01-01", 1.0], ["2025-01-02", 2.0]);

        var option = EChartsOptionBuilder.Build(spec, result);

        option["xAxis"]!["type"]!.GetValue<string>().ShouldBe("time");
        option["series"]![0]!["data"]![0]!.ToJsonString().ShouldBe("[\"2025-01-01\",1]");
        option["dataZoom"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Bar_ManyCategories_AddsZoom()
    {
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("store"), new FieldRef("revenue", Agg.Sum)));
        var rows = Enumerable.Range(1, EChartsOptionBuilder.ZoomThreshold + 1).Select(i => new object?[] { $"Store {i:00}", (double)i }).ToArray();
        var result = Result([Column("x", ColumnType.String, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y)], rows);

        EChartsOptionBuilder.Build(spec, result).ContainsKey("dataZoom").ShouldBeTrue();
    }

    [Fact]
    public void Point_ManyPoints_UsesLargeMode()
    {
        var spec = Spec(Mark.Point, new VizEncoding(new FieldRef("quantity"), new FieldRef("revenue")));
        var rows = Enumerable.Range(0, EChartsOptionBuilder.LargeThreshold + 1).Select(i => new object?[] { (long)(i % 10), (double)i }).ToArray();
        var result = Result([Column("x", ColumnType.Integer, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y)], rows);

        var series = EChartsOptionBuilder.Build(spec, result)["series"]![0]!;

        series["large"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Point_LargeChart_SizesEverySeriesAlike()
    {
        // 2,500 "Store" points and 100 "Online" points: both series must use the same size and render mode.
        var spec = Spec(Mark.Point, new VizEncoding(new FieldRef("cost"), new FieldRef("revenue"), new FieldRef("channel")));
        var rows = Enumerable.Range(0, 2_600).Select(i => new object?[] { (double)i, (double)i * 2, i < 2_500 ? "Store" : "Online" }).ToArray();
        var result = Result([Column("x", ColumnType.Number, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y), Column("color", ColumnType.String, spec.Encoding.Color)], rows);

        var series = EChartsOptionBuilder.Build(spec, result)["series"]!.AsArray();

        series.Select(s => s!["symbolSize"]!.GetValue<int>()).Distinct().ShouldHaveSingleItem();
        series.ShouldAllBe(s => s!["large"]!.GetValue<bool>());
    }

    [Fact]
    public void Heatmap_DarkTheme_HighValuesAreBrightest()
    {
        var spec = Spec(Mark.Heatmap, new VizEncoding(new FieldRef("region"), new FieldRef("channel"), new FieldRef("revenue", Agg.Sum)));
        var result = Result([Column("x", ColumnType.String, spec.Encoding.X), Column("y", ColumnType.String, spec.Encoding.Y), Column("color", ColumnType.Number, spec.Encoding.Color)], ["West", "Online", 1.0]);

        var scale = Strings(EChartsOptionBuilder.Build(spec, result, dark: true)["visualMap"]!["inRange"]!["color"]!);

        scale[^1].ShouldBe("#93c5fd"); // light blue on a dark background
    }

    [Fact]
    public void DarkTheme_ChangesColorsNotData()
    {
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("region"), new FieldRef("revenue", Agg.Sum)));
        var result = Result([Column("x", ColumnType.String, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y)], ["West", 1.0]);

        var light = EChartsOptionBuilder.Build(spec, result, dark: false);
        var dark = EChartsOptionBuilder.Build(spec, result, dark: true);

        light["textStyle"]!["color"]!.GetValue<string>().ShouldNotBe(dark["textStyle"]!["color"]!.GetValue<string>());
        JsonNode.DeepEquals(light["series"], dark["series"]).ShouldBeTrue();
    }

    [Fact]
    public void Table_IsNotAnEChartsMark()
    {
        var spec = Spec(Mark.Table, new VizEncoding(new FieldRef("region"), null));
        var result = Result([Column("x", ColumnType.String, spec.Encoding.X)], ["West"]);

        EChartsOptionBuilder.Supports(Mark.Table).ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => EChartsOptionBuilder.Build(spec, result));
    }

    [Theory]
    [InlineData("revenue", Agg.None, null, "Revenue")]
    [InlineData("orders.unit_price", Agg.Avg, null, "Unit price (avg)")]
    [InlineData("customer_id", Agg.CountDistinct, null, "Customer id (distinct count)")]
    [InlineData("order_date", Agg.None, TimeUnit.Quarter, "Order date (quarter)")]
    public void Title_IsHumanReadable(string field, Agg agg, TimeUnit? unit, string expected) =>
        ChartLabels.Title(new FieldRef(field, agg, unit)).ShouldBe(expected);

    [Theory]
    [InlineData("2025-02-01", TimeUnit.Year, "2025")]
    [InlineData("2025-05-01", TimeUnit.Quarter, "2025 Q2")]
    [InlineData("2025-02-01", TimeUnit.Month, "Feb 2025")]
    [InlineData("2025-01-06", TimeUnit.Week, "2025-W02")]
    [InlineData("2025-02-03", TimeUnit.Day, "2025-02-03")]
    public void Category_FormatsTimeUnits(string date, TimeUnit unit, string expected) =>
        ChartLabels.Category(date, ColumnType.Date, unit).ShouldBe(expected);

    private static string[] Strings(JsonNode node) => node.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static VizSpec Spec(Mark mark, VizEncoding encoding, IReadOnlyList<SortSpec>? sort = null) =>
        new(VizSpec.CurrentSchemaVersion, Dataset, mark, encoding, [], sort);

    private static QueryColumn Column(string channel, ColumnType type, FieldRef? field) => new(channel, type, channel, field);

    /// <summary>Builds a result and round-trips it through JSON so cells are JsonElements, exactly as the Web receives them.</summary>
    private static QueryResult Result(IReadOnlyList<QueryColumn> columns, params object?[][] rows)
    {
        var original = new QueryResult(columns, rows.Select(r => (IReadOnlyList<object?>)r).ToList(), Truncated: false);
        var json = JsonSerializer.Serialize(original, ContractsJsonContext.Default.QueryResult);
        return JsonSerializer.Deserialize(json, ContractsJsonContext.Default.QueryResult)!;
    }
}
