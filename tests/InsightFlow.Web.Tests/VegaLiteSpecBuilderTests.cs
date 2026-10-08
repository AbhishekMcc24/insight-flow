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
/// VizSpec + QueryResult â†’ Vega-Lite golden files (Golden/*.verified.json). Results are shaped exactly like the
/// query engine returns them: one column per channel, named after the channel, and round-tripped through the
/// contracts JSON context so cells are <see cref="JsonElement"/>s, as in the Web app.
/// </summary>
public sealed class VegaLiteSpecBuilderTests
{
    private static readonly Guid Dataset = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Bar_CategoryBySum_MatchesGolden()
    {
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("region"), new FieldRef("revenue", Agg.Sum)),
            sort: [new SortSpec("revenue", SortDirection.Desc)]);
        var result = Result(
            [Column("x", ColumnType.String, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y)],
            ["West", 1250.5], ["East", 980.25], ["North", null]);

        Golden.MatchJson(VegaLiteSpecBuilder.BuildJson(spec, result), "bar_region_sum_revenue");
    }

    [Fact]
    public void Line_MonthlyByColor_MatchesGolden()
    {
        var spec = Spec(Mark.Line, new VizEncoding(
            new FieldRef("order_date", TimeUnit: TimeUnit.Month), new FieldRef("orders.quantity", Agg.Count), new FieldRef("channel")));
        var result = Result(
            [Column("x", ColumnType.Date, spec.Encoding.X), Column("y", ColumnType.Integer, spec.Encoding.Y), Column("color", ColumnType.String, spec.Encoding.Color)],
            ["2025-01-01", 12L, "Online"], ["2025-01-01", 7L, "Store"], ["2025-02-01", 15L, "Online"]);

        Golden.MatchJson(VegaLiteSpecBuilder.BuildJson(spec, result), "line_month_count_by_channel");
    }

    [Fact]
    public void Pie_ValueByCategory_MatchesGolden()
    {
        var spec = Spec(Mark.Pie, new VizEncoding(null, new FieldRef("revenue", Agg.Sum), new FieldRef("category")));
        var result = Result(
            [Column("y", ColumnType.Number, spec.Encoding.Y), Column("color", ColumnType.String, spec.Encoding.Color)],
            [500.0, "Toys"], [300.0, "Books"]);

        Golden.MatchJson(VegaLiteSpecBuilder.BuildJson(spec, result), "pie_revenue_by_category");
    }

    [Fact]
    public void Heatmap_TwoCategoriesByMeasure_MatchesGolden()
    {
        var spec = Spec(Mark.Heatmap, new VizEncoding(new FieldRef("region"), new FieldRef("channel"), new FieldRef("revenue", Agg.Avg)));
        var result = Result(
            [Column("x", ColumnType.String, spec.Encoding.X), Column("y", ColumnType.String, spec.Encoding.Y), Column("color", ColumnType.Number, spec.Encoding.Color)],
            ["West", "Online", 10.5], ["West", "Store", 8.0], ["East", "Online", 12.25]);

        Golden.MatchJson(VegaLiteSpecBuilder.BuildJson(spec, result), "heatmap_region_channel_avg_revenue");
    }

    [Fact]
    public void DarkTheme_ChangesOnlyTheConfig()
    {
        var spec = Spec(Mark.Point, new VizEncoding(new FieldRef("quantity"), new FieldRef("revenue")));
        var result = Result([Column("x", ColumnType.Integer, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y)], [1L, 2.5]);

        var light = VegaLiteSpecBuilder.Build(spec, result, dark: false);
        var dark = VegaLiteSpecBuilder.Build(spec, result, dark: true);

        light["config"]!["axis"]!["labelColor"]!.GetValue<string>().ShouldNotBe(dark["config"]!["axis"]!["labelColor"]!.GetValue<string>());
        light.Remove("config");
        dark.Remove("config");
        JsonNode.DeepEquals(light, dark).ShouldBeTrue();
    }

    [Fact]
    public void Table_IsNotAVegaMark()
    {
        var spec = Spec(Mark.Table, new VizEncoding(new FieldRef("region"), null));
        var result = Result([Column("x", ColumnType.String, spec.Encoding.X)], ["West"]);

        VegaLiteSpecBuilder.Supports(Mark.Table).ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => VegaLiteSpecBuilder.Build(spec, result));
    }

    [Fact]
    public void NominalAxes_KeepTheEngineRowOrder()
    {
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("region"), new FieldRef("revenue", Agg.Sum)));
        var result = Result([Column("x", ColumnType.String, spec.Encoding.X), Column("y", ColumnType.Number, spec.Encoding.Y)], ["B", 1.0], ["A", 2.0]);

        var built = VegaLiteSpecBuilder.Build(spec, result);

        built["encoding"]!["x"]!.AsObject().ContainsKey("sort").ShouldBeTrue();
        built["encoding"]!["x"]!["sort"].ShouldBeNull();
        built["data"]!["values"]![0]!["x"]!.GetValue<string>().ShouldBe("B");
    }

    [Theory]
    [InlineData("revenue", Agg.None, null, "Revenue")]
    [InlineData("orders.unit_price", Agg.Avg, null, "Unit price (avg)")]
    [InlineData("customer_id", Agg.CountDistinct, null, "Customer id (distinct count)")]
    [InlineData("order_date", Agg.None, TimeUnit.Quarter, "Order date (quarter)")]
    public void Title_IsHumanReadable(string field, Agg agg, TimeUnit? unit, string expected) =>
        VegaLiteSpecBuilder.Title(new FieldRef(field, agg, unit)).ShouldBe(expected);

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
