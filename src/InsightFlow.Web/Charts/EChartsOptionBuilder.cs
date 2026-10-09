using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Viz;

namespace InsightFlow.Web.Charts;

/// <summary>
/// Turns a <see cref="VizSpec"/> plus the rows the query engine returned for it into an Apache ECharts 6 option
/// (ADR 0028). Pure and deterministic (golden-tested): result columns are matched to channels by
/// <see cref="QueryColumn.Channel"/>, colour series are pivoted here, and categories keep the SQL order (the compiler's
/// ORDER BY is the sort) except stacked bars, which are ordered by their totals. Number formatting and other functions
/// that JSON cannot carry are added by <c>wwwroot/js/echartsInterop.js</c>.
/// <para>Supported marks: bar (vertical/horizontal, stacked by colour), line, area (gradient, stacked), point (scatter),
/// pie (donut), heatmap. <see cref="Mark.Table"/> is rendered by <c>DataTable</c> instead.
/// TODO(dev2): facets (matrix/small multiples), size/label channels, box plot, cross-filter events.</para>
/// </summary>
public static class EChartsOptionBuilder
{
    /// <summary>Categorical palette (Tailwind-like hues); the brand blue comes first.</summary>
    public static readonly IReadOnlyList<string> Palette =
        ["#3b6ee0", "#f28e2b", "#2fa37c", "#e15759", "#8e6ad8", "#d4a72c", "#4fb3d9", "#b07aa1", "#ff9da7", "#9c755f"];

    /// <summary>Above this many points per series, line/scatter switch to ECharts' large-data rendering.</summary>
    public const int LargeThreshold = 2_000;

    /// <summary>Category axes with more points than this get zoom (wheel/drag + slider).</summary>
    public const int ZoomThreshold = 24;

    public static bool Supports(Mark mark) => mark != Mark.Table;

    public static string BuildJson(VizSpec spec, QueryResult result, bool dark = false) =>
        Build(spec, result, dark).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static JsonObject Build(VizSpec spec, QueryResult result, bool dark = false)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(result);
        if (!Supports(spec.Mark))
        {
            throw new NotSupportedException($"Mark '{spec.Mark}' is not rendered with ECharts.");
        }

        var theme = Theme.For(dark);
        var data = ChartData.From(spec, result);
        var option = Base(theme);

        switch (spec.Mark)
        {
            case Mark.Pie:
                Pie(option, data, theme);
                break;
            case Mark.Heatmap:
                Heatmap(option, data, theme);
                break;
            case Mark.Point:
                Scatter(option, data, theme);
                break;
            default:
                Cartesian(option, spec, data, theme);
                break;
        }

        return option;
    }

    // ---------------------------------------------------------------------------------------------
    // Marks
    // ---------------------------------------------------------------------------------------------

    /// <summary>Bar, line and area: one category (or time) axis, one value axis, a series per colour value.</summary>
    private static void Cartesian(JsonObject option, VizSpec spec, ChartData data, Theme theme)
    {
        var bar = spec.Mark == Mark.Bar;
        var area = spec.Mark == Mark.Area;

        // A bar with a numeric x and a categorical y is a horizontal bar chart.
        var horizontal = bar && data.X is { IsQuantitative: true } && data.Y is { IsQuantitative: false };
        var category = horizontal ? data.Y : data.X;
        var value = horizontal ? data.X : data.Y;
        if (category is null || value is null)
        {
            return;
        }

        var timeAxis = !bar && category.IsTemporal && category.Unit is null;
        var stacked = data.Color is not null && (bar || area);
        var groups = data.GroupByColor();

        JsonObject categoryAxis;
        var seriesData = new List<JsonArray>();
        if (timeAxis)
        {
            // Raw dates on a continuous time axis: [iso, value] pairs.
            categoryAxis = Axis(theme, "time", category.Title, onXAxis: true, isCategory: true);
            foreach (var group in groups)
            {
                seriesData.Add(new JsonArray(group.Rows.Select(r => (JsonNode?)new JsonArray(r.XText, Num(r.Value(value)))).ToArray()));
            }
        }
        else
        {
            var categories = data.Categories(category, orderByTotalOf: stacked && spec.Sort is [_] ? value : null, descending: spec.Sort is [{ Direction: SortDirection.Desc }]);
            categoryAxis = Axis(theme, "category", category.Title, onXAxis: !horizontal, isCategory: true);
            categoryAxis["data"] = new JsonArray(categories.Select(c => (JsonNode?)c).ToArray());
            categoryAxis["boundaryGap"] = bar;
            foreach (var group in groups)
            {
                var byCategory = new Dictionary<string, double?>(StringComparer.Ordinal);
                foreach (var row in group.Rows)
                {
                    byCategory.TryAdd(row.Label(category), row.Value(value));
                }

                seriesData.Add(new JsonArray(categories.Select(c => Num(byCategory.GetValueOrDefault(c))).ToArray()));
            }

            if (categories.Count > ZoomThreshold)
            {
                option["dataZoom"] = DataZoom(theme, horizontal ? "yAxisIndex" : "xAxisIndex");
            }
        }

        if (timeAxis)
        {
            option["dataZoom"] = DataZoom(theme, "xAxisIndex");
        }

        var valueAxis = Axis(theme, "value", value.Title, onXAxis: horizontal, isCategory: false);
        option["xAxis"] = horizontal ? valueAxis : categoryAxis;
        option["yAxis"] = horizontal ? categoryAxis : valueAxis;
        option["tooltip"] = Tooltip(theme, bar ? "shadow" : "line");

        var series = new JsonArray();
        for (var i = 0; i < groups.Count; i++)
        {
            var color = Palette[i % Palette.Count];
            var s = new JsonObject
            {
                ["id"] = $"s{i}",
                ["name"] = groups[i].Name ?? value.Title,
                ["type"] = bar ? "bar" : "line",
                ["data"] = seriesData[i],
                ["emphasis"] = new JsonObject { ["focus"] = "series" },
                ["universalTransition"] = new JsonObject { ["enabled"] = true },
            };

            if (stacked)
            {
                s["stack"] = "total";
            }

            if (bar)
            {
                s["barMaxWidth"] = 56;
                // Round only the outer end of the bar (the last segment of a stack).
                var outer = !stacked || i == groups.Count - 1;
                s["itemStyle"] = new JsonObject
                {
                    ["borderRadius"] = outer ? (horizontal ? Radius(0, 6, 6, 0) : Radius(6, 6, 0, 0)) : JsonValue.Create(0),
                };
            }
            else
            {
                var points = seriesData[i].Count;
                s["smooth"] = true;
                s["smoothMonotone"] = "x"; // smooth without overshooting the real values
                s["symbol"] = "circle";
                s["symbolSize"] = 6;
                s["showSymbol"] = points <= 48;
                s["lineStyle"] = new JsonObject { ["width"] = 2.5 };
                if (points > LargeThreshold)
                {
                    s["sampling"] = "lttb";
                }

                if (area)
                {
                    s["areaStyle"] = new JsonObject { ["color"] = Gradient(color, stacked ? 0.55 : 0.4, 0.04) };
                }
            }

            series.Add(s);
        }

        option["series"] = series;
        if (groups.Count > 1)
        {
            option["legend"] = Legend(theme, vertical: false);
        }

        // Axis labels and titles are contained by the grid (outerBounds), so bottom/right only reserve room for the slider.
        option["grid"] = Grid(top: groups.Count > 1 ? 40 : 28, bottom: option.ContainsKey("dataZoom") && !horizontal ? 36 : 8, right: option.ContainsKey("dataZoom") && horizontal ? 36 : 20);
    }

    /// <summary>Pie (drawn as a donut): slices by category, sized by the measure, in SQL order so the legend matches.</summary>
    private static void Pie(JsonObject option, ChartData data, Theme theme)
    {
        var category = data.Color ?? data.X;
        var value = data.Y;
        if (category is null || value is null)
        {
            return;
        }

        var slices = new JsonArray();
        foreach (var row in data.Rows)
        {
            slices.Add(new JsonObject { ["name"] = row.Label(category), ["value"] = Num(row.Value(value)) });
        }

        option["tooltip"] = new JsonObject
        {
            ["trigger"] = "item",
            ["confine"] = true,
            ["backgroundColor"] = theme.TooltipBackground,
            ["borderWidth"] = 0,
            ["textStyle"] = new JsonObject { ["color"] = theme.TooltipText },
            ["extraCssText"] = TooltipCss,
        };
        option["legend"] = Legend(theme, vertical: true);
        option["series"] = new JsonArray(new JsonObject
        {
            ["id"] = "s0",
            ["name"] = value.Title,
            ["type"] = "pie",
            ["radius"] = new JsonArray("42%", "72%"),
            ["center"] = new JsonArray("42%", "52%"),
            ["avoidLabelOverlap"] = true,
            ["padAngle"] = 1.5,
            ["itemStyle"] = new JsonObject { ["borderRadius"] = 6, ["borderColor"] = theme.Surface, ["borderWidth"] = 2 },
            ["label"] = new JsonObject { ["show"] = true, ["formatter"] = "{b}\n{d}%", ["color"] = theme.Text, ["fontSize"] = 11 },
            ["labelLine"] = new JsonObject { ["length"] = 8, ["length2"] = 8, ["lineStyle"] = new JsonObject { ["color"] = theme.Grid } },
            ["emphasis"] = new JsonObject
            {
                ["scale"] = true,
                ["scaleSize"] = 6,
                ["label"] = new JsonObject { ["fontWeight"] = "bold" },
            },
            ["data"] = slices,
            ["universalTransition"] = new JsonObject { ["enabled"] = true },
        });
    }

    /// <summary>Heatmap: two categorical axes and a continuous colour scale for the measure.</summary>
    private static void Heatmap(JsonObject option, ChartData data, Theme theme)
    {
        if (data.X is not { } x || data.Y is not { } y || data.Color is not { } measure)
        {
            return;
        }

        var xs = data.Categories(x);
        var ys = data.Categories(y);
        var cells = new JsonArray();
        double? min = null, max = null;
        foreach (var row in data.Rows)
        {
            var v = row.Value(measure);
            if (v is { } d)
            {
                min = min is null ? d : Math.Min(min.Value, d);
                max = max is null ? d : Math.Max(max.Value, d);
            }

            // Named cells so the tooltip shows both categories, e.g. "Garden · North".
            cells.Add(new JsonObject
            {
                ["name"] = $"{row.Label(x)} · {row.Label(y)}",
                ["value"] = new JsonArray(xs.IndexOf(row.Label(x)), ys.IndexOf(row.Label(y)), Num(v)),
            });
        }

        var xAxis = Axis(theme, "category", x.Title, onXAxis: true, isCategory: true);
        xAxis["data"] = new JsonArray(xs.Select(c => (JsonNode?)c).ToArray());
        xAxis["splitArea"] = new JsonObject { ["show"] = false };
        var yAxis = Axis(theme, "category", y.Title, onXAxis: false, isCategory: true);
        yAxis["data"] = new JsonArray(ys.Select(c => (JsonNode?)c).ToArray());

        option["xAxis"] = xAxis;
        option["yAxis"] = yAxis;
        option["tooltip"] = new JsonObject
        {
            ["trigger"] = "item",
            ["confine"] = true,
            ["backgroundColor"] = theme.TooltipBackground,
            ["borderWidth"] = 0,
            ["textStyle"] = new JsonObject { ["color"] = theme.TooltipText },
            ["extraCssText"] = TooltipCss,
        };
        option["visualMap"] = new JsonObject
        {
            ["type"] = "continuous",
            ["min"] = min ?? 0,
            ["max"] = max ?? 1,
            ["calculable"] = true,
            ["orient"] = "vertical",
            ["right"] = 0,
            ["top"] = "middle",
            ["itemHeight"] = 140,
            ["text"] = new JsonArray(measure.Title, ""),
            ["textStyle"] = new JsonObject { ["color"] = theme.Text, ["fontSize"] = 11 },
            ["inRange"] = new JsonObject { ["color"] = new JsonArray(theme.HeatScale.Select(c => (JsonNode?)c).ToArray()) },
            ["outOfRange"] = new JsonObject { ["color"] = theme.Grid },
        };
        option["grid"] = Grid(top: 24, bottom: 8, right: 110);
        option["series"] = new JsonArray(new JsonObject
        {
            ["id"] = "s0",
            ["name"] = measure.Title,
            ["type"] = "heatmap",
            ["data"] = cells,
            ["itemStyle"] = new JsonObject { ["borderColor"] = theme.Surface, ["borderWidth"] = 2, ["borderRadius"] = 4 },
            ["emphasis"] = new JsonObject
            {
                ["itemStyle"] = new JsonObject { ["shadowBlur"] = 12, ["shadowColor"] = "rgba(15, 23, 42, 0.35)" },
            },
            ["universalTransition"] = new JsonObject { ["enabled"] = true },
        });
    }

    /// <summary>Scatter: value (or time/category) x against value y, a series per colour value.</summary>
    private static void Scatter(JsonObject option, ChartData data, Theme theme)
    {
        if (data.X is not { } x || data.Y is not { } y)
        {
            return;
        }

        var xType = x.IsQuantitative ? "value" : x.IsTemporal && x.Unit is null ? "time" : "category";
        var groups = data.GroupByColor();
        var xAxis = Axis(theme, xType, x.Title, onXAxis: true, isCategory: xType != "value");
        if (xType == "category")
        {
            xAxis["data"] = new JsonArray(data.Categories(x).Select(c => (JsonNode?)c).ToArray());
        }
        else
        {
            xAxis["scale"] = true;
        }

        var yAxis = Axis(theme, "value", y.Title, onXAxis: false, isCategory: false);
        yAxis["scale"] = true;
        option["xAxis"] = xAxis;
        option["yAxis"] = yAxis;
        option["tooltip"] = new JsonObject
        {
            ["trigger"] = "item",
            ["confine"] = true,
            ["backgroundColor"] = theme.TooltipBackground,
            ["borderWidth"] = 0,
            ["textStyle"] = new JsonObject { ["color"] = theme.TooltipText },
            ["extraCssText"] = TooltipCss,
        };
        option["dataZoom"] = new JsonArray(
            new JsonObject { ["type"] = "inside", ["xAxisIndex"] = 0, ["filterMode"] = "none" },
            new JsonObject { ["type"] = "inside", ["yAxisIndex"] = 0, ["filterMode"] = "none" });

        // Size and render mode depend on the whole chart, so every series looks the same.
        var large = data.Rows.Count > LargeThreshold;
        var series = new JsonArray();
        for (var i = 0; i < groups.Count; i++)
        {
            var points = new JsonArray();
            foreach (var row in groups[i].Rows)
            {
                JsonNode? xv = xType == "value" ? Num(row.Value(x)) : JsonValue.Create(xType == "time" ? row.XText : row.Label(x));
                points.Add(new JsonArray(xv, Num(row.Value(y))));
            }

            var s = new JsonObject
            {
                ["id"] = $"s{i}",
                ["name"] = groups[i].Name ?? y.Title,
                ["type"] = "scatter",
                ["data"] = points,
                ["symbolSize"] = large ? 5 : 9,
                ["itemStyle"] = new JsonObject { ["opacity"] = 0.75 },
                ["emphasis"] = new JsonObject { ["focus"] = "series", ["scale"] = 1.6 },
                ["universalTransition"] = new JsonObject { ["enabled"] = true },
            };
            if (large)
            {
                s["large"] = true;
                s["largeThreshold"] = LargeThreshold;
            }

            series.Add(s);
        }

        option["series"] = series;
        if (groups.Count > 1)
        {
            option["legend"] = Legend(theme, vertical: false);
        }

        option["grid"] = Grid(top: groups.Count > 1 ? 40 : 28, bottom: 8, right: 20);
    }

    // ---------------------------------------------------------------------------------------------
    // Shared pieces
    // ---------------------------------------------------------------------------------------------

    private const string TooltipCss = "border-radius: 8px; box-shadow: 0 8px 24px rgba(15, 23, 42, 0.18); padding: 8px 12px;";

    private static JsonObject Base(Theme theme) => new()
    {
        ["backgroundColor"] = "transparent",
        ["color"] = new JsonArray(Palette.Select(c => (JsonNode?)c).ToArray()),
        ["textStyle"] = new JsonObject { ["fontFamily"] = "Inter, ui-sans-serif, system-ui, sans-serif", ["color"] = theme.Text },
        ["animationDuration"] = 600,
        ["animationDurationUpdate"] = 450,
        ["animationEasing"] = "cubicOut",
        ["animationEasingUpdate"] = "cubicInOut",
        ["aria"] = new JsonObject { ["enabled"] = true },
        ["toolbox"] = new JsonObject
        {
            ["right"] = 4,
            ["top"] = 0,
            ["itemSize"] = 13,
            ["iconStyle"] = new JsonObject { ["borderColor"] = theme.Muted },
            ["emphasis"] = new JsonObject { ["iconStyle"] = new JsonObject { ["borderColor"] = Palette[0] } },
            ["feature"] = new JsonObject
            {
                ["saveAsImage"] = new JsonObject { ["title"] = "Save as PNG", ["pixelRatio"] = 2, ["backgroundColor"] = theme.Surface },
            },
        },
    };

    private static JsonObject Axis(Theme theme, string type, string title, bool onXAxis, bool isCategory)
    {
        var axis = new JsonObject
        {
            ["type"] = type,
            ["name"] = title,
            ["nameLocation"] = "middle",
            // x-axis titles sit under one row of labels; y-axis titles beside (wider) value labels.
            ["nameGap"] = onXAxis ? 30 : 48,
            ["nameTextStyle"] = new JsonObject { ["color"] = theme.Muted, ["fontWeight"] = 500, ["fontSize"] = 12 },
            ["axisLine"] = new JsonObject { ["show"] = isCategory, ["lineStyle"] = new JsonObject { ["color"] = theme.Grid } },
            ["axisTick"] = new JsonObject { ["show"] = false },
            ["axisLabel"] = new JsonObject { ["color"] = theme.Text, ["fontSize"] = 11, ["hideOverlap"] = true },
            ["splitLine"] = new JsonObject
            {
                ["show"] = !isCategory,
                ["lineStyle"] = new JsonObject { ["color"] = theme.Grid, ["type"] = "dashed" },
            },
        };
        return axis;
    }

    private static JsonObject Tooltip(Theme theme, string pointer) => new()
    {
        ["trigger"] = "axis",
        ["confine"] = true,
        ["axisPointer"] = new JsonObject
        {
            ["type"] = pointer,
            ["shadowStyle"] = new JsonObject { ["color"] = theme.PointerShade },
            ["lineStyle"] = new JsonObject { ["color"] = theme.Muted, ["type"] = "dashed" },
        },
        ["backgroundColor"] = theme.TooltipBackground,
        ["borderWidth"] = 0,
        ["textStyle"] = new JsonObject { ["color"] = theme.TooltipText },
        ["extraCssText"] = TooltipCss,
    };

    private static JsonObject Legend(Theme theme, bool vertical) => new()
    {
        ["type"] = "scroll",
        ["orient"] = vertical ? "vertical" : "horizontal",
        ["left"] = vertical ? "auto" : 0,
        ["right"] = vertical ? 4 : "auto",
        ["top"] = vertical ? "middle" : 0,
        ["icon"] = "roundRect",
        ["itemWidth"] = 12,
        ["itemHeight"] = 8,
        ["itemGap"] = 14,
        ["textStyle"] = new JsonObject { ["color"] = theme.Text, ["fontSize"] = 12 },
        ["pageTextStyle"] = new JsonObject { ["color"] = theme.Muted },
    };

    private static JsonArray DataZoom(Theme theme, string axisIndex)
    {
        var horizontalSlider = axisIndex == "xAxisIndex";
        var slider = new JsonObject
        {
            ["type"] = "slider",
            [axisIndex] = 0,
            [horizontalSlider ? "height" : "width"] = 18,
            [horizontalSlider ? "bottom" : "right"] = 8,
            ["brushSelect"] = false,
            ["borderColor"] = "transparent",
            ["backgroundColor"] = theme.ZoomTrack,
            ["fillerColor"] = theme.ZoomFill,
            ["dataBackground"] = new JsonObject
            {
                ["lineStyle"] = new JsonObject { ["color"] = theme.Muted, ["opacity"] = 0.4 },
                ["areaStyle"] = new JsonObject { ["color"] = theme.Muted, ["opacity"] = 0.12 },
            },
            ["handleStyle"] = new JsonObject { ["color"] = Palette[0], ["borderColor"] = Palette[0] },
            ["moveHandleSize"] = 0,
            ["textStyle"] = new JsonObject { ["color"] = theme.Muted, ["fontSize"] = 10 },
        };
        return new JsonArray(new JsonObject { ["type"] = "inside", [axisIndex] = 0, ["filterMode"] = "none" }, slider);
    }

    private static JsonObject Grid(int top, int bottom, int right) => new()
    {
        ["left"] = 12,
        ["right"] = right,
        ["top"] = top,
        ["bottom"] = bottom,
        // ECharts 6 replacement for the deprecated `containLabel: true`.
        ["outerBoundsMode"] = "same",
        ["outerBoundsContain"] = "all",
    };

    private static JsonArray Radius(int a, int b, int c, int d) => new JsonArray(a, b, c, d);

    private static JsonObject Gradient(string hex, double topAlpha, double bottomAlpha) => new()
    {
        ["type"] = "linear",
        ["x"] = 0,
        ["y"] = 0,
        ["x2"] = 0,
        ["y2"] = 1,
        ["colorStops"] = new JsonArray(
            new JsonObject { ["offset"] = 0, ["color"] = Rgba(hex, topAlpha) },
            new JsonObject { ["offset"] = 1, ["color"] = Rgba(hex, bottomAlpha) }),
    };

    private static string Rgba(string hex, double alpha)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return string.Create(CultureInfo.InvariantCulture, $"rgba({r}, {g}, {b}, {alpha})");
    }

    private static JsonValue? Num(double? value) => value is { } d ? JsonValue.Create(Math.Round(d, 6)) : null;

    // ---------------------------------------------------------------------------------------------
    // Theme (mirrors the Tailwind tokens in Styles/app.css)
    // ---------------------------------------------------------------------------------------------

    private sealed record Theme(
        string Text, string Muted, string Grid, string Surface, string TooltipBackground, string TooltipText,
        string PointerShade, string ZoomTrack, string ZoomFill, IReadOnlyList<string> HeatScale)
    {
        public static Theme For(bool dark) => dark
            ? new("#cbd5e1", "#94a3b8", "#1e293b", "#0f172a", "rgba(15, 23, 42, 0.96)", "#e2e8f0",
                "rgba(148, 163, 184, 0.12)", "#1e293b", "rgba(59, 110, 224, 0.25)",
                // Dark mode: low values recede into the background, high values are the brightest.
                ["#1e293b", "#1e3a8a", "#3b6ee0", "#93c5fd"])
            : new("#475569", "#64748b", "#e2e8f0", "#ffffff", "rgba(255, 255, 255, 0.98)", "#0f172a",
                "rgba(100, 116, 139, 0.08)", "#f1f5f9", "rgba(59, 110, 224, 0.18)",
                ["#eef3ff", "#7ea6f0", "#3b6ee0", "#1e3a8a"]);
    }

    // ---------------------------------------------------------------------------------------------
    // Result â†’ channels
    // ---------------------------------------------------------------------------------------------

    private sealed record ChannelInfo(int Index, string Title, ColumnType Type, TimeUnit? Unit)
    {
        public bool IsQuantitative => Type is ColumnType.Integer or ColumnType.Number;

        public bool IsTemporal => Type is ColumnType.Date or ColumnType.DateTime;
    }

    private sealed record Row(IReadOnlyList<object?> Cells, ChannelInfo? X)
    {
        public double? Value(ChannelInfo channel) => ChartLabels.Number(Cell(channel));

        public string Label(ChannelInfo channel) => ChartLabels.Category(Cell(channel), channel.Type, channel.Unit);

        public string? XText => X is null ? null : ChartLabels.Text(Cell(X));

        private object? Cell(ChannelInfo channel) => channel.Index < Cells.Count ? Cells[channel.Index] : null;
    }

    private sealed record Group(string? Name, List<Row> Rows);

    private sealed class ChartData
    {
        public ChannelInfo? X { get; private init; }

        public ChannelInfo? Y { get; private init; }

        public ChannelInfo? Color { get; private init; }

        public List<Row> Rows { get; private init; } = [];

        public static ChartData From(VizSpec spec, QueryResult result)
        {
            ChannelInfo? Channel(string name, FieldRef? field)
            {
                if (field is null)
                {
                    return null;
                }

                for (var i = 0; i < result.Columns.Count; i++)
                {
                    if (result.Columns[i].Channel == name)
                    {
                        return new ChannelInfo(i, ChartLabels.Title(field), result.Columns[i].Type, field.TimeUnit);
                    }
                }

                return null;
            }

            var x = Channel("x", spec.Encoding.X);
            return new ChartData
            {
                X = x,
                Y = Channel("y", spec.Encoding.Y),
                Color = Channel("color", spec.Encoding.Color),
                Rows = result.Rows.Select(r => new Row(r, x)).ToList(),
            };
        }

        /// <summary>Distinct labels in row order, or ordered by the per-category total of <paramref name="orderByTotalOf"/>.</summary>
        public List<string> Categories(ChannelInfo channel, ChannelInfo? orderByTotalOf = null, bool descending = true)
        {
            var labels = Rows.Select(r => r.Label(channel)).Distinct(StringComparer.Ordinal).ToList();
            if (orderByTotalOf is null)
            {
                return labels;
            }

            var totals = Rows.GroupBy(r => r.Label(channel), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Value(orderByTotalOf) ?? 0), StringComparer.Ordinal);
            return descending
                ? labels.OrderByDescending(l => totals[l]).ToList()
                : labels.OrderBy(l => totals[l]).ToList();
        }

        /// <summary>One group per colour value (first-appearance order), or a single unnamed group without colour.</summary>
        public List<Group> GroupByColor()
        {
            if (Color is null)
            {
                return [new Group(null, [.. Rows])];
            }

            var groups = new List<Group>();
            var index = new Dictionary<string, Group>(StringComparer.Ordinal);
            foreach (var row in Rows)
            {
                var name = row.Label(Color);
                if (!index.TryGetValue(name, out var group))
                {
                    group = new Group(name, []);
                    index[name] = group;
                    groups.Add(group);
                }

                group.Rows.Add(row);
            }

            return groups;
        }
    }
}
