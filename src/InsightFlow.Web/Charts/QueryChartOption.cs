using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InsightFlow.Contracts.Query;

namespace InsightFlow.Web.Charts;

/// <summary>
/// A simple chart for the answer to a question, when the rows have the shape of one: a label or date column followed by one or more
/// number columns, a few rows. Anything else is shown as a table only. Pure, so it is unit-tested; the JSON goes to ECharts.
/// </summary>
public static class QueryChartOption
{
    public const int MinRows = 2;
    public const int MaxRows = 60;
    public const int MaxSeries = 4;

    /// <summary>True when <see cref="Build"/> would produce a chart for this result.</summary>
    public static bool CanChart(QueryResult? result) => Shape(result) is not null;

    public static string? Build(QueryResult result, bool dark)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (Shape(result) is not { } shape)
        {
            return null;
        }

        var text = dark ? "#cbd5e1" : "#475569";
        var line = dark ? "#334155" : "#dce7ea";
        var categories = new JsonArray();
        foreach (var row in result.Rows)
        {
            categories.Add(Label(row[0]));
        }

        var series = new JsonArray();
        foreach (var index in shape.ValueColumns)
        {
            var data = new JsonArray();
            foreach (var row in result.Rows)
            {
                var value = Number(row[index]);
                data.Add(value is { } d ? JsonValue.Create(d) : null);
            }

            series.Add(new JsonObject
            {
                ["name"] = result.Columns[index].Name,
                ["type"] = shape.IsTime ? "line" : "bar",
                ["smooth"] = shape.IsTime,
                ["data"] = data,
                ["emphasis"] = new JsonObject { ["focus"] = "series" },
            });
        }

        var option = new JsonObject
        {
            ["color"] = new JsonArray([.. EChartsOptionBuilder.Palette.Select(c => (JsonNode?)JsonValue.Create(c))]),
            ["textStyle"] = new JsonObject { ["fontFamily"] = "Inter Variable, sans-serif" },
            ["tooltip"] = new JsonObject { ["trigger"] = "axis", ["axisPointer"] = new JsonObject { ["type"] = shape.IsTime ? "line" : "shadow" } },
            ["grid"] = new JsonObject { ["left"] = 12, ["right"] = 16, ["top"] = shape.ValueColumns.Count > 1 ? 40 : 16, ["bottom"] = 12, ["containLabel"] = true },
            ["xAxis"] = new JsonObject
            {
                ["type"] = "category",
                ["data"] = categories,
                ["axisLabel"] = new JsonObject { ["color"] = text, ["hideOverlap"] = true, ["rotate"] = categories.Count > 12 ? 40 : 0 },
                ["axisLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = line } },
            },
            ["yAxis"] = new JsonObject
            {
                ["type"] = "value",
                ["axisLabel"] = new JsonObject { ["color"] = text },
                ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = line } },
            },
            ["series"] = series,
        };
        if (shape.ValueColumns.Count > 1)
        {
            option["legend"] = new JsonObject { ["top"] = 0, ["textStyle"] = new JsonObject { ["color"] = text } };
        }

        return option.ToJsonString();
    }

    private sealed record ChartShape(IReadOnlyList<int> ValueColumns, bool IsTime);

    private static ChartShape? Shape(QueryResult? result)
    {
        if (result is null || result.Columns.Count < 2 || result.Rows.Count is < MinRows or > MaxRows)
        {
            return null;
        }

        var first = result.Columns[0].Type;
        if (first is not (ColumnType.String or ColumnType.Date or ColumnType.DateTime or ColumnType.Integer))
        {
            return null;
        }

        var values = Enumerable.Range(1, result.Columns.Count - 1)
            .Where(i => result.Columns[i].Type is ColumnType.Integer or ColumnType.Number)
            .Take(MaxSeries)
            .ToList();
        return values.Count == 0 ? null : new ChartShape(values, first is ColumnType.Date or ColumnType.DateTime);
    }

    private static string Label(object? value) => value switch
    {
        null => "—",
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "—",
        JsonElement { ValueKind: JsonValueKind.Null } => "—",
        JsonElement e => e.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—",
    };

    private static double? Number(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetDouble(out var d) => d,
        JsonElement => null,
        IConvertible c => Convert.ToDouble(c, CultureInfo.InvariantCulture),
        _ => null,
    };
}
