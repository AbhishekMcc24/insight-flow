using System.Text.RegularExpressions;

namespace InsightFlow.Domain.Contracts.Query;

/// <summary>
/// How to draw an answer: the chart <see cref="Type"/> (one of <see cref="ChartTypes.All"/>) and which output columns feed it. Every column is
/// optional and names an output column of the query; anything missing or unknown is worked out from the rows. <see cref="X"/> is the category,
/// time or source column, <see cref="Y"/> the measure columns, <see cref="Series"/> a column that splits the data into several series (or the
/// target of a flow), <see cref="Size"/> the bubble size.
/// </summary>
public sealed record ChartSpecDto(
    string Type,
    string? X = null,
    IReadOnlyList<string>? Y = null,
    string? Series = null,
    string? Size = null,
    string? Title = null);

/// <summary>One chart type a person can ask for.</summary>
public sealed record ChartTypeInfo(string Key, string Label, string Group);

/// <summary>
/// Every chart type the system can draw, and a plain-language reader that finds which one a person asked for ("as a pie chart", "show a
/// funnel"), so an explicit request is honoured whatever the model suggested.
/// </summary>
public static partial class ChartTypes
{
    public static readonly IReadOnlyList<ChartTypeInfo> All =
    [
        new("bar", "Bar", "Comparison"),
        new("hbar", "Horizontal bar", "Comparison"),
        new("stackedbar", "Stacked bar", "Comparison"),
        new("combo", "Bar and line", "Comparison"),
        new("radar", "Radar", "Comparison"),
        new("polar", "Polar bar", "Comparison"),
        new("line", "Line", "Trend"),
        new("area", "Area", "Trend"),
        new("stackedarea", "Stacked area", "Trend"),
        new("themeriver", "Stream graph", "Trend"),
        new("candlestick", "Candlestick", "Trend"),
        new("calendar", "Calendar heatmap", "Trend"),
        new("pie", "Pie", "Parts of a whole"),
        new("donut", "Donut", "Parts of a whole"),
        new("rose", "Rose (Nightingale)", "Parts of a whole"),
        new("treemap", "Treemap", "Parts of a whole"),
        new("sunburst", "Sunburst", "Parts of a whole"),
        new("funnel", "Funnel", "Parts of a whole"),
        new("waterfall", "Waterfall", "Parts of a whole"),
        new("scatter", "Scatter", "Relationship"),
        new("bubble", "Bubble", "Relationship"),
        new("heatmap", "Heatmap", "Relationship"),
        new("parallel", "Parallel coordinates", "Relationship"),
        new("sankey", "Sankey", "Flow"),
        new("graph", "Network", "Flow"),
        new("histogram", "Histogram", "Distribution"),
        new("boxplot", "Box plot", "Distribution"),
        new("gauge", "Gauge", "Single value"),
    ];

    public static bool IsKnown(string? type) => type is not null && All.Any(t => string.Equals(t.Key, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>The canonical key for a type name the model or a person wrote ("Pie", "doughnut", "column"), or null when unknown.</summary>
    public static string? Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var key = type.Trim().ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        return key switch
        {
            "column" or "columns" or "vertical" or "verticalbar" or "barchart" => "bar",
            "horizontalbar" or "barh" or "rowbar" => "hbar",
            "stackedcolumn" or "stacked" => "stackedbar",
            "doughnut" or "ring" => "donut",
            "nightingale" or "roseplot" or "nightingalerose" => "rose",
            "scatterplot" or "xy" => "scatter",
            "spider" or "web" or "radarchart" => "radar",
            "speedometer" or "dial" or "meter" => "gauge",
            "tree" or "treemapchart" => "treemap",
            "network" or "networkgraph" or "nodelink" => "graph",
            "box" or "boxandwhisker" or "whisker" => "boxplot",
            "bridge" => "waterfall",
            "ohlc" or "stock" => "candlestick",
            "dualaxis" or "barline" or "barandline" or "linebar" => "combo",
            "stream" or "streamgraph" or "river" => "themeriver",
            "parallelcoordinates" => "parallel",
            "calendarheatmap" => "calendar",
            _ => IsKnown(key) ? key : null,
        };
    }

    // Ordered from the most specific wording to the least, so "stacked bar chart" is not read as a plain bar chart.
    private static readonly (Regex Pattern, string Type)[] Intents =
    [
        (Phrase(@"calendar\s*(heat\s*map|chart)"), "calendar"),
        (Phrase(@"stacked\s+(bar|column)"), "stackedbar"),
        (Phrase(@"stacked\s+area"), "stackedarea"),
        (Phrase(@"(horizontal\s+(bar|column)|bar\s+chart\s+horizontal(ly)?)"), "hbar"),
        (Phrase(@"(donut|doughnut|ring\s+chart)"), "donut"),
        (Phrase(@"(nightingale|rose\s+(chart|diagram|plot))"), "rose"),
        (Phrase(@"pie"), "pie"),
        (Phrase(@"(tree\s*map)"), "treemap"),
        (Phrase(@"sun\s*burst"), "sunburst"),
        (Phrase(@"sankey|flow\s+diagram"), "sankey"),
        (Phrase(@"(network|node[\s-]*link)\s+(graph|diagram|chart)"), "graph"),
        (Phrase(@"funnel"), "funnel"),
        (Phrase(@"(gauge|speedometer|dial\s+chart)"), "gauge"),
        (Phrase(@"(heat\s*map)"), "heatmap"),
        (Phrase(@"(bubble)"), "bubble"),
        (Phrase(@"(scatter\s*(plot|chart|graph)?)"), "scatter"),
        (Phrase(@"(radar|spider\s+(chart|graph|web)|web\s+chart)"), "radar"),
        (Phrase(@"(box\s*(and|&)?\s*whisker|box\s*plot)"), "boxplot"),
        (Phrase(@"histogram"), "histogram"),
        (Phrase(@"(waterfall|bridge\s+chart)"), "waterfall"),
        (Phrase(@"(candle\s*stick|ohlc|stock\s+chart)"), "candlestick"),
        (Phrase(@"(combo|dual[\s-]*axis|bar\s+and\s+line|line\s+and\s+bar)"), "combo"),
        (Phrase(@"polar"), "polar"),
        (Phrase(@"parallel\s+coordinates"), "parallel"),
        (Phrase(@"(theme\s*river|stream\s*graph|stream\s+chart)"), "themeriver"),
        (Phrase(@"area\s+(chart|graph)"), "area"),
        (Phrase(@"(line\s+(chart|graph|plot)|trend\s+line)"), "line"),
        (Phrase(@"(bar\s+(chart|graph|plot)|column\s+chart)"), "bar"),
    ];

    private static Regex Phrase(string pattern) =>
        new($@"\b{pattern}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>The chart type a question asks for in words ("as a pie chart", "show it as a funnel"), or null when it names none.</summary>
    public static string? FromQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return null;
        }

        try
        {
            foreach (var (pattern, type) in Intents)
            {
                if (pattern.IsMatch(question))
                {
                    return type;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A question built to stall a pattern names no chart.
        }

        return null;
    }
}
