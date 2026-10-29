using System.Text.Json;
using InsightFlow.Domain.Contracts.Query;

namespace InsightFlow.Agent.Knowledge;

/// <summary>
/// Reads the chart the model suggests for an answer and checks it against what a person asked for. A chart type the person named in words
/// ("as a pie chart") always wins over the model's choice; an unknown type is dropped. The columns are not checked here: they name output
/// columns of the SQL, which only the page that holds the rows can resolve (it falls back to the shape of the rows for any it cannot find).
/// </summary>
public static class ChartSpecReader
{
    /// <summary>The prompt text that tells the model about the "chart" field (shared by every planner that can chart).</summary>
    public const string PromptRule = """
        - "chart" says how to draw the answer, or null when a table is enough (a single value, many text columns). Shape: {"type": "<one of: bar, hbar,
          stackedbar, combo, radar, polar, line, area, stackedarea, themeriver, candlestick, calendar, pie, donut, rose, treemap, sunburst, funnel,
          waterfall, scatter, bubble, heatmap, parallel, sankey, graph, histogram, boxplot, gauge>", "x": "<category, time or source column>",
          "y": ["<measure columns>"], "series": "<column that splits the data into series, or the target of a flow>", "size": "<bubble size column>",
          "title": "<short title>"}. Use output column names exactly as aliased in your SELECT.
          When the person names a chart ("as a pie chart", "show a funnel", "scatter plot") use exactly that type and shape the SELECT to fit it: pie,
          donut, rose, funnel and treemap need one category column and one measure; scatter needs two measures; bubble three; sankey and graph a
          source column, a target column and a value; heatmap two category columns and a measure; candlestick a date and open, high, low, close;
          histogram and boxplot the raw values (not already summed). When they name none, choose the best fit: parts of a whole -> pie or donut; a trend
          over time -> line or area; a ranking or comparison -> bar or hbar; two measures -> scatter; flows -> sankey; hierarchy -> treemap or sunburst.
        """;

    /// <summary>The model's "chart" value (an object, or null), or null when absent or unusable.</summary>
    public static ChartSpecDto? Read(JsonElement root)
    {
        if (!root.TryGetProperty("chart", out var chart) || chart.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = ChartTypes.Normalize(Str(chart, "type"));
        if (type is null)
        {
            return null;
        }

        var y = new List<string>();
        if (chart.TryGetProperty("y", out var yValue))
        {
            if (yValue.ValueKind == JsonValueKind.Array)
            {
                y.AddRange(yValue.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).Take(12));
            }
            else if (yValue.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(yValue.GetString()))
            {
                y.Add(yValue.GetString()!);
            }
        }

        var title = Str(chart, "title");
        return new ChartSpecDto(type, Str(chart, "x"), y, Str(chart, "series"), Str(chart, "size"), title is { Length: > 120 } ? title[..120] : title);
    }

    /// <summary>What was asked for in words wins; otherwise the model's suggestion stands.</summary>
    public static ChartSpecDto? Apply(ChartSpecDto? suggested, string? question)
    {
        var asked = ChartTypes.FromQuestion(question);
        if (asked is null)
        {
            return suggested;
        }

        return suggested is null ? new ChartSpecDto(asked) : suggested with { Type = asked };
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
}
