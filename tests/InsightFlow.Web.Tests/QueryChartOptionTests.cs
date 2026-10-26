using System.Text.Json;
using InsightFlow.Contracts.Query;
using InsightFlow.Web.Charts;

namespace InsightFlow.Web.Tests;

public sealed class QueryChartOptionTests
{
    private static QueryResult Result(ColumnType first, params (string Name, ColumnType Type)[] values)
    {
        var columns = new List<QueryColumn> { new("label", first) };
        columns.AddRange(values.Select(v => new QueryColumn(v.Name, v.Type)));
        var rows = Enumerable.Range(0, 5)
            .Select(i => (IReadOnlyList<object?>)[.. new object?[] { $"item {i}" }.Concat(values.Select(_ => (object?)(i * 10)))])
            .ToList();
        return new QueryResult(columns, rows, false);
    }

    [Fact]
    public void Build_LabelAndOneNumber_IsABarChart()
    {
        var json = QueryChartOption.Build(Result(ColumnType.String, ("total", ColumnType.Number)), dark: false);

        using var doc = JsonDocument.Parse(json!);
        var series = doc.RootElement.GetProperty("series");
        series.GetArrayLength().ShouldBe(1);
        series[0].GetProperty("type").GetString().ShouldBe("bar");
        series[0].GetProperty("data").GetArrayLength().ShouldBe(5);
        doc.RootElement.TryGetProperty("legend", out _).ShouldBeFalse();
    }

    [Fact]
    public void Build_DateAndTwoNumbers_IsALineChartWithALegend()
    {
        var json = QueryChartOption.Build(Result(ColumnType.Date, ("a", ColumnType.Integer), ("b", ColumnType.Number)), dark: true);

        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("series")[0].GetProperty("type").GetString().ShouldBe("line");
        doc.RootElement.GetProperty("series").GetArrayLength().ShouldBe(2);
        doc.RootElement.TryGetProperty("legend", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ColumnType.Number)]   // no label column first
    [InlineData(ColumnType.Boolean)]
    public void CanChart_WrongFirstColumn_IsFalse(ColumnType first) =>
        QueryChartOption.CanChart(Result(first, ("total", ColumnType.Number))).ShouldBeFalse();

    [Fact]
    public void CanChart_NoNumberColumn_IsFalse() =>
        QueryChartOption.CanChart(Result(ColumnType.String, ("note", ColumnType.String))).ShouldBeFalse();

    [Fact]
    public void CanChart_TooFewOrTooManyRows_IsFalse()
    {
        var one = new QueryResult([new("a", ColumnType.String), new("b", ColumnType.Number)], [["x", 1]], false);
        var many = new QueryResult(
            [new("a", ColumnType.String), new("b", ColumnType.Number)],
            [.. Enumerable.Range(0, QueryChartOption.MaxRows + 1).Select(i => (IReadOnlyList<object?>)new object?[] { $"r{i}", i })],
            false);

        QueryChartOption.CanChart(one).ShouldBeFalse();
        QueryChartOption.CanChart(many).ShouldBeFalse();
        QueryChartOption.CanChart(null).ShouldBeFalse();
    }

    [Fact]
    public void Build_ValuesThatAreJsonElements_AreRead()
    {
        // After a trip over HTTP the cells arrive as JsonElement.
        using var doc = JsonDocument.Parse("""["north", 12.5]""");
        var cells = doc.RootElement.EnumerateArray().Select(e => (object?)e.Clone()).ToList();
        var result = new QueryResult([new("region", ColumnType.String), new("total", ColumnType.Number)], [cells, cells], false);

        var json = QueryChartOption.Build(result, dark: false);

        using var output = JsonDocument.Parse(json!);
        output.RootElement.GetProperty("series")[0].GetProperty("data")[0].GetDouble().ShouldBe(12.5);
        output.RootElement.GetProperty("xAxis").GetProperty("data")[0].GetString().ShouldBe("north");
    }
}
