using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InsightFlow.Domain.Contracts.Query;

namespace InsightFlow.Web.Charts;

/// <summary>
/// Draws the rows of an answer as any of the chart types in <see cref="ChartTypes.All"/>: bar, line, area, pie, donut, scatter, bubble, radar,
/// funnel, gauge, heatmap, treemap, sunburst, sankey, network, box plot, histogram, waterfall, candlestick and more. A <see cref="ChartSpecDto"/>
/// says which type and which columns; anything it leaves out is worked out from the shape of the rows (text or date columns are categories,
/// number columns are measures). Pure, so it is unit-tested; the JSON goes to ECharts.
/// </summary>
public static class ChartFactory
{
    public const int MaxCategoryRows = 60;
    public const int MaxPointRows = 2_000;
    public const int MaxSlices = 12;
    public const int MaxSeries = 12;

    /// <summary>What the columns of a result are, for deciding which charts can draw it.</summary>
    public sealed record Shape(IReadOnlyList<int> Dimensions, IReadOnlyList<int> Measures, int Rows, bool DateAxis);

    public static Shape? ShapeOf(QueryResult? result)
    {
        if (result is null || result.Columns.Count == 0 || result.Rows.Count == 0)
        {
            return null;
        }

        var measures = Enumerable.Range(0, result.Columns.Count).Where(i => IsMeasure(result, i)).ToList();
        var dims = Enumerable.Range(0, result.Columns.Count).Where(i => !measures.Contains(i)).ToList();

        // A result of numbers only (an id column and an amount): the first column labels the rows.
        if (dims.Count == 0 && measures.Count >= 2)
        {
            dims.Add(measures[0]);
            measures.RemoveAt(0);
        }

        var dateAxis = dims.Count > 0 && result.Columns[dims[0]].Type is ColumnType.Date or ColumnType.DateTime;
        return measures.Count == 0 ? null : new Shape(dims, measures, result.Rows.Count, dateAxis);
    }

    /// <summary>The chart types that can draw this result, in the order they are offered.</summary>
    public static IReadOnlyList<ChartTypeInfo> Compatible(QueryResult? result)
    {
        if (ShapeOf(result) is not { } s)
        {
            return [];
        }

        return [.. ChartTypes.All.Where(t => Supports(t.Key, s, result!))];
    }

    /// <summary>True when an automatic chart is worth showing for this result (a few rows and at least one measure).</summary>
    public static bool CanChart(QueryResult? result) => Auto(result) is not null;

    /// <summary>The type chosen with no instruction: a trend line for dates, a bar chart for categories.</summary>
    public static string? Auto(QueryResult? result)
    {
        if (ShapeOf(result) is not { } s || s.Rows < 2 || s.Rows > MaxCategoryRows || s.Dimensions.Count == 0)
        {
            return null;
        }

        return s.DateAxis ? "line" : "bar";
    }

    /// <summary>The type to show first: the requested one when it can draw the rows, else the automatic one.</summary>
    public static string? Choose(QueryResult? result, string? requested)
    {
        if (ShapeOf(result) is not { } s)
        {
            return null;
        }

        var wanted = ChartTypes.Normalize(requested);
        if (wanted is not null && Supports(wanted, s, result!))
        {
            return wanted;
        }

        return Auto(result);
    }

    public static bool Supports(string type, Shape s, QueryResult result)
    {
        var m = s.Measures.Count;
        var d = s.Dimensions.Count;
        var small = s.Rows <= MaxCategoryRows;
        return type switch
        {
            "bar" or "hbar" or "stackedbar" or "line" or "area" or "stackedarea" or "polar" => d >= 1 && small,
            "combo" => d >= 1 && m >= 2 && small,
            "pie" or "donut" or "rose" or "funnel" or "treemap" or "waterfall" => d >= 1 && s.Rows <= 500,
            "sunburst" => d >= 1 && s.Rows <= 500,
            "radar" => (m >= 3 && s.Rows <= 8) || (d >= 1 && s.Rows is >= 3 and <= 12),
            "scatter" => m >= 2 && s.Rows <= MaxPointRows,
            "bubble" => m >= 3 && s.Rows <= MaxPointRows,
            "heatmap" => (d >= 2 && s.Rows <= 1_500) || (d >= 1 && m >= 2 && small),
            "sankey" or "graph" => d >= 2 && s.Rows <= 500,
            "boxplot" => s.Rows >= 5 && s.Rows <= MaxPointRows,
            "histogram" => s.Rows >= 5 && s.Rows <= MaxPointRows,
            "candlestick" => d >= 1 && m >= 4 && s.Rows <= MaxPointRows,
            "parallel" => m >= 3 && s.Rows <= 500,
            "themeriver" => s.DateAxis && s.Rows <= MaxPointRows && (d >= 2 || m >= 2),
            "calendar" => s.DateAxis && s.Rows <= MaxPointRows,
            "gauge" => m >= 1,
            _ => false,
        } && result.Columns.Count > 0;
    }

    /// <summary>The ECharts option for <paramref name="spec"/>, or null when the rows cannot be drawn that way.</summary>
    public static string? Build(QueryResult result, ChartSpecDto? spec, bool dark)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (ShapeOf(result) is not { } shape)
        {
            return null;
        }

        var type = Choose(result, spec?.Type);
        if (type is null)
        {
            return null;
        }

        var t = new Theme(dark);
        var cols = Resolve(result, shape, spec);
        var option = type switch
        {
            "bar" or "stackedbar" or "line" or "area" or "stackedarea" or "hbar" or "combo" => Cartesian(result, shape, cols, type, t),
            "polar" => Polar(result, shape, cols, t),
            "pie" or "donut" or "rose" => Pie(result, cols, type, t),
            "funnel" => Funnel(result, cols, t),
            "treemap" => Treemap(result, cols, t),
            "sunburst" => Sunburst(result, cols, t),
            "scatter" or "bubble" => Scatter(result, shape, cols, type == "bubble", t),
            "radar" => Radar(result, shape, cols, t),
            "gauge" => Gauge(result, cols, t),
            "heatmap" => Heatmap(result, shape, cols, t),
            "sankey" => Flow(result, cols, sankey: true, t),
            "graph" => Flow(result, cols, sankey: false, t),
            "boxplot" => Box(result, cols, t),
            "histogram" => Histogram(result, cols, t),
            "waterfall" => Waterfall(result, cols, t),
            "candlestick" => Candles(result, shape, cols, t),
            "parallel" => Parallel(result, shape, t),
            "themeriver" => River(result, shape, cols, t),
            "calendar" => CalendarMap(result, cols, t),
            _ => null,
        };
        if (option is null)
        {
            return null;
        }

        option["color"] = new JsonArray([.. EChartsOptionBuilder.Palette.Select(c => (JsonNode?)JsonValue.Create(c))]);
        option["textStyle"] = new JsonObject { ["fontFamily"] = "Inter Variable, sans-serif" };
        if (!string.IsNullOrWhiteSpace(spec?.Title))
        {
            option["title"] = new JsonObject { ["text"] = spec.Title, ["left"] = "center", ["textStyle"] = new JsonObject { ["color"] = t.Text, ["fontSize"] = 14 } };
        }

        return option.ToJsonString();
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Which columns feed the chart
    // ------------------------------------------------------------------------------------------------------------------------------

    private sealed record Cols(int? X, IReadOnlyList<int> Y, int? Series, int? Size);

    private static Cols Resolve(QueryResult result, Shape shape, ChartSpecDto? spec)
    {
        int? Find(string? name) => string.IsNullOrWhiteSpace(name) ? null : result.Columns.Select((c, i) => (c, i)).FirstOrDefault(x => string.Equals(x.c.Name, name, StringComparison.OrdinalIgnoreCase)) is { c: not null } hit ? hit.i : null;

        var x = Find(spec?.X);
        var ys = (spec?.Y ?? []).Select(Find).Where(i => i is not null && IsMeasure(result, i.Value)).Select(i => i!.Value).Distinct().ToList();
        var series = Find(spec?.Series);
        var size = Find(spec?.Size);

        x ??= shape.Dimensions.Count > 0 ? shape.Dimensions[0] : null;
        if (ys.Count == 0)
        {
            ys = [.. shape.Measures.Where(i => i != x)];
        }

        // A second text column splits the data into series (or is the target of a flow).
        series ??= shape.Dimensions.Where(i => i != x).Cast<int?>().FirstOrDefault();
        if (series == x)
        {
            series = null;
        }

        return new Cols(x, ys, series, size);
    }

    private static bool IsMeasure(QueryResult result, int index) => result.Columns[index].Type is ColumnType.Integer or ColumnType.Number;

    // ------------------------------------------------------------------------------------------------------------------------------
    // Chart families
    // ------------------------------------------------------------------------------------------------------------------------------

    private static JsonObject Cartesian(QueryResult result, Shape shape, Cols cols, string type, Theme t)
    {
        var (categories, series) = Pivot(result, cols, type is "stackedbar" or "stackedarea" or "bar" or "hbar" or "line" or "area");
        var horizontal = type == "hbar";
        var stacked = type is "stackedbar" or "stackedarea";
        var isLine = type is "line" or "area" or "stackedarea";
        var combo = type == "combo";
        var out_ = new JsonArray();
        var index = 0;
        foreach (var (name, values) in series)
        {
            var node = new JsonObject
            {
                ["name"] = name,
                ["type"] = combo ? (index == 0 ? "bar" : "line") : isLine ? "line" : "bar",
                ["data"] = Numbers(values),
                ["emphasis"] = new JsonObject { ["focus"] = "series" },
            };
            if (isLine)
            {
                node["smooth"] = shape.DateAxis;
                node["showSymbol"] = categories.Count <= 40;
                if (type is "area" or "stackedarea")
                {
                    node["areaStyle"] = new JsonObject { ["opacity"] = 0.25 };
                }
            }

            if (stacked)
            {
                node["stack"] = "total";
            }

            if (combo && index > 0)
            {
                node["yAxisIndex"] = 1;
                node["smooth"] = true;
            }

            out_.Add(node);
            index++;
        }

        var cat = new JsonObject { ["type"] = "category", ["data"] = Strings(categories), ["axisLabel"] = Label(t, categories.Count), ["axisLine"] = Line(t), ["boundaryGap"] = !isLine };
        var val = (string? name) => new JsonObject { ["type"] = "value", ["name"] = name, ["axisLabel"] = new JsonObject { ["color"] = t.Text }, ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = t.Line } }, ["nameTextStyle"] = new JsonObject { ["color"] = t.Text } };
        var option = new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "axis", ["axisPointer"] = new JsonObject { ["type"] = isLine ? "line" : "shadow" } },
            ["grid"] = new JsonObject { ["left"] = 12, ["right"] = combo ? 28 : 16, ["top"] = series.Count > 1 ? 40 : 16, ["bottom"] = 12, ["containLabel"] = true },
            ["series"] = out_,
        };
        if (horizontal)
        {
            cat["inverse"] = true;
            cat["axisLabel"] = new JsonObject { ["color"] = t.Text, ["hideOverlap"] = false, ["rotate"] = 0 };
            option["xAxis"] = val(null);
            option["yAxis"] = cat;
        }
        else
        {
            option["xAxis"] = cat;
            option["yAxis"] = combo && series.Count > 1 ? new JsonArray(val(series[0].Name), val(series[1].Name)) : val(null);
        }

        if (series.Count > 1)
        {
            option["legend"] = Legend(t);
        }

        return option;
    }

    private static JsonObject Polar(QueryResult result, Shape shape, Cols cols, Theme t)
    {
        var (categories, series) = Pivot(result, cols, true);
        var out_ = new JsonArray();
        foreach (var (name, values) in series)
        {
            out_.Add(new JsonObject { ["name"] = name, ["type"] = "bar", ["coordinateSystem"] = "polar", ["data"] = Numbers(values), ["stack"] = "p" });
        }

        var option = new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "item" },
            ["polar"] = new JsonObject { ["radius"] = new JsonArray(20, "75%") },
            ["angleAxis"] = new JsonObject { ["type"] = "category", ["data"] = Strings(categories), ["axisLabel"] = new JsonObject { ["color"] = t.Text } },
            ["radiusAxis"] = new JsonObject { ["axisLabel"] = new JsonObject { ["color"] = t.Text }, ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = t.Line } } },
            ["series"] = out_,
        };
        if (series.Count > 1)
        {
            option["legend"] = Legend(t);
        }

        _ = shape;
        return option;
    }

    private static JsonObject Pie(QueryResult result, Cols cols, string type, Theme t)
    {
        var slices = Slices(result, cols);
        var data = new JsonArray([.. slices.Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["value"] = s.Value })]);
        var radius = type switch
        {
            "donut" => new JsonArray("45%", "72%"),
            "rose" => new JsonArray("15%", "72%"),
            _ => new JsonArray(0, "72%"),
        };
        var series = new JsonObject
        {
            ["type"] = "pie",
            ["radius"] = radius,
            ["center"] = new JsonArray("50%", "52%"),
            ["data"] = data,
            ["label"] = new JsonObject { ["color"] = t.Text, ["formatter"] = "{b}: {d}%" },
            ["labelLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = t.Line } },
            ["emphasis"] = new JsonObject { ["itemStyle"] = new JsonObject { ["shadowBlur"] = 12, ["shadowColor"] = "rgba(0,0,0,0.25)" } },
        };
        if (type == "rose")
        {
            series["roseType"] = "radius";
            series["itemStyle"] = new JsonObject { ["borderRadius"] = 6 };
        }
        else if (type == "donut")
        {
            series["itemStyle"] = new JsonObject { ["borderRadius"] = 6, ["borderColor"] = t.Surface, ["borderWidth"] = 2 };
        }

        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "item", ["formatter"] = "{b}<br/>{c} ({d}%)" },
            ["legend"] = new JsonObject { ["type"] = "scroll", ["bottom"] = 0, ["textStyle"] = new JsonObject { ["color"] = t.Text } },
            ["series"] = new JsonArray(series),
        };
    }

    private static JsonObject Funnel(QueryResult result, Cols cols, Theme t)
    {
        var slices = Slices(result, cols).OrderByDescending(s => s.Value).ToList();
        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "item", ["formatter"] = "{b}: {c}" },
            ["legend"] = new JsonObject { ["type"] = "scroll", ["bottom"] = 0, ["textStyle"] = new JsonObject { ["color"] = t.Text } },
            ["series"] = new JsonArray(new JsonObject
            {
                ["type"] = "funnel",
                ["left"] = "10%",
                ["width"] = "80%",
                ["top"] = 12,
                ["bottom"] = 40,
                ["sort"] = "descending",
                ["gap"] = 2,
                ["label"] = new JsonObject { ["position"] = "inside", ["color"] = "#fff", ["formatter"] = "{b}" },
                ["data"] = new JsonArray([.. slices.Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["value"] = s.Value })]),
            }),
        };
    }

    private static JsonObject Treemap(QueryResult result, Cols cols, Theme t)
    {
        var data = Hierarchy(result, cols);
        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["formatter"] = "{b}: {c}" },
            ["series"] = new JsonArray(new JsonObject
            {
                ["type"] = "treemap",
                ["roam"] = false,
                ["nodeClick"] = "zoomToNode",
                ["breadcrumb"] = new JsonObject { ["show"] = true },
                ["label"] = new JsonObject { ["color"] = "#fff", ["formatter"] = "{b}" },
                ["upperLabel"] = new JsonObject { ["show"] = true, ["color"] = "#fff" },
                ["itemStyle"] = new JsonObject { ["borderColor"] = t.Surface, ["borderWidth"] = 2, ["gapWidth"] = 2 },
                ["data"] = data,
            }),
        };
    }

    private static JsonObject Sunburst(QueryResult result, Cols cols, Theme t)
    {
        _ = t;
        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "item", ["formatter"] = "{b}: {c}" },
            ["series"] = new JsonArray(new JsonObject
            {
                ["type"] = "sunburst",
                ["radius"] = new JsonArray(0, "85%"),
                ["label"] = new JsonObject { ["rotate"] = "radial", ["minAngle"] = 8 },
                ["itemStyle"] = new JsonObject { ["borderRadius"] = 4, ["borderWidth"] = 2 },
                ["data"] = Hierarchy(result, cols),
            }),
        };
    }

    private static JsonObject Scatter(QueryResult result, Shape shape, Cols cols, bool bubble, Theme t)
    {
        var measures = shape.Measures.ToList();
        var xi = cols.X is { } cx && IsMeasure(result, cx) ? cx : measures[0];
        var yi = cols.Y.FirstOrDefault(i => i != xi, measures.First(i => i != xi));
        var si = bubble ? (cols.Size ?? measures.FirstOrDefault(i => i != xi && i != yi, -1)) : -1;
        var labelColumn = shape.Dimensions.Count > 0 ? shape.Dimensions[0] : (int?)null;

        var rows = result.Rows.Select(r => (X: Number(r[xi]), Y: Number(r[yi]), S: si >= 0 ? Number(r[si]) : null, Name: labelColumn is { } l ? Text(r[l]) : null, Group: cols.Series is { } g ? Text(r[g]) : null))
            .Where(p => p.X is not null && p.Y is not null)
            .ToList();
        var maxSize = rows.Max(p => p.S) ?? 1;
        double Size(double? value) => !bubble || value is null || maxSize <= 0 ? 10 : 8 + 40 * Math.Sqrt(Math.Max(0, value.Value) / maxSize);

        var groups = rows.GroupBy(p => cols.Series is null ? "" : p.Group ?? "—").Take(MaxSeries).ToList();
        var series = new JsonArray();
        foreach (var group in groups)
        {
            series.Add(new JsonObject
            {
                ["name"] = group.Key.Length == 0 ? result.Columns[yi].Name : group.Key,
                ["type"] = "scatter",
                ["large"] = rows.Count > 1_500,
                ["data"] = new JsonArray([.. group.Select(p => (JsonNode?)new JsonObject
                {
                    ["name"] = p.Name ?? string.Empty,
                    ["value"] = new JsonArray(p.X, p.Y, p.S),
                    ["symbolSize"] = Size(p.S),
                })]),
                ["itemStyle"] = new JsonObject { ["opacity"] = bubble ? 0.7 : 0.85 },
            });
        }

        var option = new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "item", ["formatter"] = $"{{a}}<br/>{{b}}<br/>{result.Columns[xi].Name}: {{@[0]}}<br/>{result.Columns[yi].Name}: {{@[1]}}" },
            ["grid"] = new JsonObject { ["left"] = 12, ["right"] = 24, ["top"] = groups.Count > 1 ? 40 : 24, ["bottom"] = 12, ["containLabel"] = true },
            ["xAxis"] = new JsonObject { ["type"] = "value", ["name"] = result.Columns[xi].Name, ["nameLocation"] = "middle", ["nameGap"] = 28, ["scale"] = true, ["axisLabel"] = new JsonObject { ["color"] = t.Text }, ["nameTextStyle"] = new JsonObject { ["color"] = t.Text }, ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = t.Line } } },
            ["yAxis"] = new JsonObject { ["type"] = "value", ["name"] = result.Columns[yi].Name, ["scale"] = true, ["axisLabel"] = new JsonObject { ["color"] = t.Text }, ["nameTextStyle"] = new JsonObject { ["color"] = t.Text }, ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = t.Line } } },
            ["series"] = series,
        };
        if (groups.Count > 1)
        {
            option["legend"] = Legend(t);
        }

        return option;
    }

    private static JsonObject Radar(QueryResult result, Shape shape, Cols cols, Theme t)
    {
        var byMeasures = shape.Measures.Count >= 3 && result.Rows.Count <= 8;
        JsonArray indicators;
        var series = new JsonArray();
        if (byMeasures)
        {
            var measures = shape.Measures.Take(10).ToList();
            indicators = new JsonArray([.. measures.Select(i => (JsonNode?)new JsonObject { ["name"] = result.Columns[i].Name, ["max"] = Max(result, i) })]);
            var labelColumn = shape.Dimensions.Count > 0 ? shape.Dimensions[0] : (int?)null;
            var data = new JsonArray();
            var n = 0;
            foreach (var row in result.Rows)
            {
                data.Add(new JsonObject { ["name"] = labelColumn is { } l ? Text(row[l]) : $"Row {++n}", ["value"] = new JsonArray([.. measures.Select(i => (JsonNode?)(Number(row[i]) ?? 0))]) });
            }

            series.Add(new JsonObject { ["type"] = "radar", ["data"] = data, ["areaStyle"] = new JsonObject { ["opacity"] = 0.15 } });
        }
        else
        {
            var x = cols.X ?? shape.Dimensions[0];
            var rows = result.Rows.Take(12).ToList();
            var ys = cols.Y.Take(MaxSeries).ToList();
            var max = ys.Max(i => Max(result, i));
            indicators = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonObject { ["name"] = Text(r[x]), ["max"] = max })]);
            series.Add(new JsonObject
            {
                ["type"] = "radar",
                ["areaStyle"] = new JsonObject { ["opacity"] = 0.15 },
                ["data"] = new JsonArray([.. ys.Select(i => (JsonNode?)new JsonObject { ["name"] = result.Columns[i].Name, ["value"] = new JsonArray([.. rows.Select(r => (JsonNode?)(Number(r[i]) ?? 0))]) })]),
            });
        }

        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "item" },
            ["legend"] = new JsonObject { ["bottom"] = 0, ["type"] = "scroll", ["textStyle"] = new JsonObject { ["color"] = t.Text } },
            ["radar"] = new JsonObject
            {
                ["indicator"] = indicators,
                ["radius"] = "65%",
                ["axisName"] = new JsonObject { ["color"] = t.Text },
                ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = t.Line } },
                ["splitArea"] = new JsonObject { ["show"] = false },
                ["axisLine"] = Line(t),
            },
            ["series"] = series,
        };
    }

    private static JsonObject Gauge(QueryResult result, Cols cols, Theme t)
    {
        var measure = cols.Y.Count > 0 ? cols.Y[0] : Enumerable.Range(0, result.Columns.Count).First(i => IsMeasure(result, i));
        var value = Number(result.Rows[0][measure]) ?? 0;
        var max = value <= 100 ? 100d : Math.Pow(10, Math.Ceiling(Math.Log10(Math.Max(1, value))));
        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["formatter"] = "{b}: {c}" },
            ["series"] = new JsonArray(new JsonObject
            {
                ["type"] = "gauge",
                ["max"] = max,
                ["progress"] = new JsonObject { ["show"] = true, ["width"] = 14 },
                ["axisLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["width"] = 14, ["color"] = new JsonArray(new JsonArray(1, t.Line)) } },
                ["axisLabel"] = new JsonObject { ["color"] = t.Text },
                ["detail"] = new JsonObject { ["valueAnimation"] = true, ["color"] = t.Text, ["fontSize"] = 26, ["offsetCenter"] = new JsonArray(0, "70%") },
                ["title"] = new JsonObject { ["color"] = t.Text, ["offsetCenter"] = new JsonArray(0, "95%") },
                ["data"] = new JsonArray(new JsonObject { ["name"] = result.Columns[measure].Name, ["value"] = value }),
            }),
        };
    }

    private static JsonObject? Heatmap(QueryResult result, Shape shape, Cols cols, Theme t)
    {
        List<string> xs;
        List<string> ys;
        var cells = new JsonArray();
        double min;
        double max;
        if (shape.Dimensions.Count >= 2)
        {
            var xi = cols.X ?? shape.Dimensions[0];
            var yi = cols.Series ?? shape.Dimensions[1];
            var mi = cols.Y.Count > 0 ? cols.Y[0] : shape.Measures[0];
            xs = [.. result.Rows.Select(r => Text(r[xi])).Distinct().Take(40)];
            ys = [.. result.Rows.Select(r => Text(r[yi])).Distinct().Take(40)];
            var sums = new Dictionary<(int, int), double>();
            foreach (var row in result.Rows)
            {
                var a = xs.IndexOf(Text(row[xi]));
                var b = ys.IndexOf(Text(row[yi]));
                if (a >= 0 && b >= 0 && Number(row[mi]) is { } v)
                {
                    sums[(a, b)] = sums.GetValueOrDefault((a, b)) + v;
                }
            }

            foreach (var ((a, b), v) in sums)
            {
                cells.Add(new JsonArray(a, b, v));
            }

            min = sums.Count == 0 ? 0 : sums.Values.Min();
            max = sums.Count == 0 ? 1 : sums.Values.Max();
        }
        else
        {
            var labelColumn = cols.X ?? shape.Dimensions[0];
            var measures = shape.Measures.Take(30).ToList();
            xs = [.. measures.Select(i => result.Columns[i].Name)];
            ys = [.. result.Rows.Select(r => Text(r[labelColumn]))];
            var all = new List<double>();
            for (var rowIndex = 0; rowIndex < result.Rows.Count; rowIndex++)
            {
                for (var m = 0; m < measures.Count; m++)
                {
                    if (Number(result.Rows[rowIndex][measures[m]]) is { } v)
                    {
                        cells.Add(new JsonArray(m, rowIndex, v));
                        all.Add(v);
                    }
                }
            }

            min = all.Count == 0 ? 0 : all.Min();
            max = all.Count == 0 ? 1 : all.Max();
        }

        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["position"] = "top" },
            ["grid"] = new JsonObject { ["left"] = 12, ["right"] = 16, ["top"] = 12, ["bottom"] = 56, ["containLabel"] = true },
            ["xAxis"] = new JsonObject { ["type"] = "category", ["data"] = Strings(xs), ["splitArea"] = new JsonObject { ["show"] = true }, ["axisLabel"] = Label(t, xs.Count) },
            ["yAxis"] = new JsonObject { ["type"] = "category", ["data"] = Strings(ys), ["inverse"] = true, ["axisLabel"] = new JsonObject { ["color"] = t.Text } },
            ["visualMap"] = new JsonObject
            {
                ["min"] = min,
                ["max"] = max <= min ? min + 1 : max,
                ["calculable"] = true,
                ["orient"] = "horizontal",
                ["left"] = "center",
                ["bottom"] = 0,
                ["textStyle"] = new JsonObject { ["color"] = t.Text },
                ["inRange"] = new JsonObject { ["color"] = new JsonArray("#e6f4f4", "#62c9c7", "#176b73") },
            },
            ["series"] = new JsonArray(new JsonObject { ["type"] = "heatmap", ["data"] = cells, ["label"] = new JsonObject { ["show"] = cells.Count <= 120, ["color"] = "#0f172a" }, ["emphasis"] = new JsonObject { ["itemStyle"] = new JsonObject { ["shadowBlur"] = 8 } } }),
        };
    }

    private static JsonObject? Flow(QueryResult result, Cols cols, bool sankey, Theme t)
    {
        var source = cols.X;
        var target = cols.Series;
        var value = cols.Y.Count > 0 ? cols.Y[0] : (int?)null;
        if (source is null || target is null || value is null)
        {
            return null;
        }

        var edges = result.Rows
            .Select(r => (From: Text(r[source.Value]), To: Text(r[target.Value]), Value: Number(r[value.Value]) ?? 0))
            .Where(e => e.Value > 0 && e.From != e.To)
            .GroupBy(e => (e.From, e.To))
            .Select(g => (g.Key.From, g.Key.To, Value: g.Sum(e => e.Value)))
            .ToList();
        if (sankey)
        {
            edges = DropCycles(edges);
        }

        var names = edges.SelectMany(e => new[] { e.From, e.To }).Distinct().ToList();
        if (edges.Count == 0)
        {
            return null;
        }

        var links = new JsonArray([.. edges.Select(e => (JsonNode?)new JsonObject { ["source"] = e.From, ["target"] = e.To, ["value"] = e.Value })]);
        if (sankey)
        {
            return new JsonObject
            {
                ["tooltip"] = new JsonObject { ["trigger"] = "item", ["triggerOn"] = "mousemove" },
                ["series"] = new JsonArray(new JsonObject
                {
                    ["type"] = "sankey",
                    ["left"] = 12,
                    ["right"] = 90,
                    ["top"] = 12,
                    ["bottom"] = 12,
                    ["emphasis"] = new JsonObject { ["focus"] = "adjacency" },
                    ["lineStyle"] = new JsonObject { ["color"] = "gradient", ["curveness"] = 0.5 },
                    ["label"] = new JsonObject { ["color"] = t.Text },
                    ["data"] = new JsonArray([.. names.Select(n => (JsonNode?)new JsonObject { ["name"] = n })]),
                    ["links"] = links,
                }),
            };
        }

        var degree = names.ToDictionary(n => n, n => edges.Count(e => e.From == n || e.To == n));
        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "item" },
            ["series"] = new JsonArray(new JsonObject
            {
                ["type"] = "graph",
                ["layout"] = "force",
                ["roam"] = true,
                ["draggable"] = true,
                ["force"] = new JsonObject { ["repulsion"] = 220, ["edgeLength"] = new JsonArray(60, 160) },
                ["label"] = new JsonObject { ["show"] = true, ["color"] = t.Text },
                ["edgeSymbol"] = new JsonArray("none", "arrow"),
                ["edgeSymbolSize"] = new JsonArray(0, 8),
                ["lineStyle"] = new JsonObject { ["color"] = "source", ["curveness"] = 0.15, ["opacity"] = 0.6 },
                ["data"] = new JsonArray([.. names.Select(n => (JsonNode?)new JsonObject { ["name"] = n, ["symbolSize"] = 14 + Math.Min(40, degree[n] * 5) })]),
                ["links"] = links,
            }),
        };
    }

    private static JsonObject? Box(QueryResult result, Cols cols, Theme t)
    {
        var measure = cols.Y.Count > 0 ? cols.Y[0] : (int?)null;
        if (measure is null)
        {
            return null;
        }

        var groups = cols.X is { } x && !IsMeasure(result, x)
            ? result.Rows.GroupBy(r => Text(r[x])).Take(20).Select(g => (Name: g.Key, Values: g.Select(r => Number(r[measure.Value])).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList())).ToList()
            : [(result.Columns[measure.Value].Name, result.Rows.Select(r => Number(r[measure.Value])).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList())];
        groups = [.. groups.Where(g => g.Values.Count >= 1)];
        if (groups.Count == 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "item" },
            ["grid"] = new JsonObject { ["left"] = 12, ["right"] = 16, ["top"] = 16, ["bottom"] = 12, ["containLabel"] = true },
            ["xAxis"] = new JsonObject { ["type"] = "category", ["data"] = Strings([.. groups.Select(g => g.Name)]), ["axisLabel"] = Label(t, groups.Count) },
            ["yAxis"] = new JsonObject { ["type"] = "value", ["scale"] = true, ["name"] = result.Columns[measure.Value].Name, ["axisLabel"] = new JsonObject { ["color"] = t.Text }, ["nameTextStyle"] = new JsonObject { ["color"] = t.Text }, ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = t.Line } } },
            ["series"] = new JsonArray(new JsonObject
            {
                ["type"] = "boxplot",
                ["data"] = new JsonArray([.. groups.Select(g => (JsonNode?)new JsonArray(Quantile(g.Values, 0), Quantile(g.Values, 0.25), Quantile(g.Values, 0.5), Quantile(g.Values, 0.75), Quantile(g.Values, 1)))]),
            }),
        };
    }

    private static JsonObject? Histogram(QueryResult result, Cols cols, Theme t)
    {
        var measure = cols.Y.Count > 0 ? cols.Y[0] : (int?)null;
        if (measure is null)
        {
            return null;
        }

        var values = result.Rows.Select(r => Number(r[measure.Value])).Where(v => v is not null).Select(v => v!.Value).ToList();
        if (values.Count < 2)
        {
            return null;
        }

        var bins = Math.Clamp((int)Math.Ceiling(Math.Log2(values.Count) + 1), 5, 30);
        var low = values.Min();
        var high = values.Max();
        var width = high > low ? (high - low) / bins : 1;
        var counts = new int[bins];
        foreach (var v in values)
        {
            counts[Math.Min(bins - 1, (int)((v - low) / width))]++;
        }

        var labels = Enumerable.Range(0, bins).Select(i => $"{Format(low + (i * width))}–{Format(low + ((i + 1) * width))}").ToList();
        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "axis", ["axisPointer"] = new JsonObject { ["type"] = "shadow" } },
            ["grid"] = new JsonObject { ["left"] = 12, ["right"] = 16, ["top"] = 16, ["bottom"] = 12, ["containLabel"] = true },
            ["xAxis"] = new JsonObject { ["type"] = "category", ["data"] = Strings(labels), ["name"] = result.Columns[measure.Value].Name, ["nameLocation"] = "middle", ["nameGap"] = 34, ["axisLabel"] = Label(t, labels.Count), ["nameTextStyle"] = new JsonObject { ["color"] = t.Text } },
            ["yAxis"] = new JsonObject { ["type"] = "value", ["name"] = "Count", ["axisLabel"] = new JsonObject { ["color"] = t.Text }, ["nameTextStyle"] = new JsonObject { ["color"] = t.Text }, ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = t.Line } } },
            ["series"] = new JsonArray(new JsonObject { ["type"] = "bar", ["barCategoryGap"] = "4%", ["data"] = new JsonArray([.. counts.Select(c => (JsonNode?)c)]) }),
        };
    }

    private static JsonObject? Waterfall(QueryResult result, Cols cols, Theme t)
    {
        if (cols.X is not { } x || cols.Y.Count == 0)
        {
            return null;
        }

        var m = cols.Y[0];
        var categories = new List<string>();
        var bases = new List<double?>();
        var ups = new List<double?>();
        var downs = new List<double?>();
        var totals = new List<double?>();
        double running = 0;
        foreach (var row in result.Rows.Take(MaxCategoryRows))
        {
            var delta = Number(row[m]) ?? 0;
            categories.Add(Text(row[x]));
            if (delta >= 0)
            {
                bases.Add(running);
                ups.Add(delta);
                downs.Add(null);
            }
            else
            {
                bases.Add(running + delta);
                ups.Add(null);
                downs.Add(-delta);
            }

            totals.Add(null);
            running += delta;
        }

        categories.Add("Total");
        bases.Add(0);
        ups.Add(null);
        downs.Add(null);
        totals.Add(running);

        JsonObject Stack(string name, List<double?> values, string? color, bool invisible = false)
        {
            var node = new JsonObject { ["name"] = name, ["type"] = "bar", ["stack"] = "w", ["data"] = Numbers(values) };
            if (invisible)
            {
                node["itemStyle"] = new JsonObject { ["borderColor"] = "transparent", ["color"] = "transparent" };
                node["emphasis"] = new JsonObject { ["itemStyle"] = new JsonObject { ["borderColor"] = "transparent", ["color"] = "transparent" } };
            }
            else
            {
                node["itemStyle"] = new JsonObject { ["color"] = color };
            }

            return node;
        }

        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "axis", ["axisPointer"] = new JsonObject { ["type"] = "shadow" } },
            ["legend"] = new JsonObject { ["top"] = 0, ["data"] = new JsonArray("Increase", "Decrease", "Total"), ["textStyle"] = new JsonObject { ["color"] = t.Text } },
            ["grid"] = new JsonObject { ["left"] = 12, ["right"] = 16, ["top"] = 40, ["bottom"] = 12, ["containLabel"] = true },
            ["xAxis"] = new JsonObject { ["type"] = "category", ["data"] = Strings(categories), ["axisLabel"] = Label(t, categories.Count) },
            ["yAxis"] = new JsonObject { ["type"] = "value", ["axisLabel"] = new JsonObject { ["color"] = t.Text }, ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = t.Line } } },
            ["series"] = new JsonArray(Stack("Base", bases, null, invisible: true), Stack("Increase", ups, "#3ba272"), Stack("Decrease", downs, "#d8605b"), Stack("Total", totals, "#176b73")),
        };
    }

    private static JsonObject? Candles(QueryResult result, Shape shape, Cols cols, Theme t)
    {
        if (cols.X is not { } x)
        {
            return null;
        }

        int? Named(string name) => shape.Measures.Cast<int?>().FirstOrDefault(i => result.Columns[i!.Value].Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        var measures = shape.Measures.Where(i => i != x).ToList();
        int open = Named("open") ?? measures[0];
        int high = Named("high") ?? measures[1];
        int low = Named("low") ?? measures[2];
        int close = Named("close") ?? measures[3];
        var categories = result.Rows.Select(r => Text(r[x])).ToList();
        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "axis", ["axisPointer"] = new JsonObject { ["type"] = "cross" } },
            ["grid"] = new JsonObject { ["left"] = 12, ["right"] = 16, ["top"] = 16, ["bottom"] = 12, ["containLabel"] = true },
            ["xAxis"] = new JsonObject { ["type"] = "category", ["data"] = Strings(categories), ["axisLabel"] = Label(t, categories.Count) },
            ["yAxis"] = new JsonObject { ["type"] = "value", ["scale"] = true, ["axisLabel"] = new JsonObject { ["color"] = t.Text }, ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = t.Line } } },
            ["series"] = new JsonArray(new JsonObject
            {
                ["type"] = "candlestick",
                ["itemStyle"] = new JsonObject { ["color"] = "#3ba272", ["color0"] = "#d8605b", ["borderColor"] = "#3ba272", ["borderColor0"] = "#d8605b" },
                ["data"] = new JsonArray([.. result.Rows.Select(r => (JsonNode?)new JsonArray(Number(r[open]), Number(r[close]), Number(r[low]), Number(r[high])))]),
            }),
        };
    }

    private static JsonObject Parallel(QueryResult result, Shape shape, Theme t)
    {
        var measures = shape.Measures.Take(10).ToList();
        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "item" },
            ["parallel"] = new JsonObject { ["left"] = 40, ["right"] = 40, ["top"] = 24, ["bottom"] = 24, ["parallelAxisDefault"] = new JsonObject { ["nameTextStyle"] = new JsonObject { ["color"] = t.Text }, ["axisLabel"] = new JsonObject { ["color"] = t.Text }, ["axisLine"] = Line(t) } },
            ["parallelAxis"] = new JsonArray([.. measures.Select((m, i) => (JsonNode?)new JsonObject { ["dim"] = i, ["name"] = result.Columns[m].Name })]),
            ["series"] = new JsonArray(new JsonObject
            {
                ["type"] = "parallel",
                ["lineStyle"] = new JsonObject { ["width"] = 1.5, ["opacity"] = 0.45 },
                ["data"] = new JsonArray([.. result.Rows.Select(r => (JsonNode?)new JsonArray([.. measures.Select(m => (JsonNode?)(Number(r[m]) ?? 0))]))]),
            }),
        };
    }

    private static JsonObject? River(QueryResult result, Shape shape, Cols cols, Theme t)
    {
        if (cols.X is not { } x)
        {
            return null;
        }

        var points = new List<(string Date, double Value, string Name)>();
        if (cols.Series is { } s && cols.Y.Count > 0)
        {
            points.AddRange(result.Rows.Select(r => (Date(r[x]), Number(r[cols.Y[0]]) ?? 0, Text(r[s]))));
        }
        else
        {
            foreach (var m in cols.Y.Take(MaxSeries))
            {
                points.AddRange(result.Rows.Select(r => (Date(r[x]), Number(r[m]) ?? 0, result.Columns[m].Name)));
            }
        }

        var names = points.Select(p => p.Name).Distinct().Take(MaxSeries).ToList();
        _ = shape;
        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["trigger"] = "axis", ["axisPointer"] = new JsonObject { ["type"] = "line" } },
            ["legend"] = new JsonObject { ["top"] = 0, ["data"] = Strings(names), ["textStyle"] = new JsonObject { ["color"] = t.Text } },
            ["singleAxis"] = new JsonObject { ["type"] = "time", ["top"] = 40, ["bottom"] = 24, ["axisLabel"] = new JsonObject { ["color"] = t.Text } },
            ["series"] = new JsonArray(new JsonObject
            {
                ["type"] = "themeRiver",
                ["emphasis"] = new JsonObject { ["itemStyle"] = new JsonObject { ["shadowBlur"] = 12, ["shadowColor"] = "rgba(0,0,0,0.3)" } },
                ["data"] = new JsonArray([.. points.Where(p => names.Contains(p.Name)).Select(p => (JsonNode?)new JsonArray(p.Date, p.Value, p.Name))]),
            }),
        };
    }

    private static JsonObject? CalendarMap(QueryResult result, Cols cols, Theme t)
    {
        if (cols.X is not { } x || cols.Y.Count == 0)
        {
            return null;
        }

        var days = result.Rows.Select(r => (Day: Date(r[x]), Value: Number(r[cols.Y[0]]) ?? 0)).GroupBy(p => p.Day).Select(g => (Day: g.Key, Value: g.Sum(p => p.Value))).OrderBy(p => p.Day, StringComparer.Ordinal).ToList();
        if (days.Count == 0)
        {
            return null;
        }

        var last = days[^1].Day;
        var start = days[0].Day;
        // Only the latest year fits a calendar; an earlier start is cut to it.
        var range = string.CompareOrdinal(start, string.Concat(last.AsSpan(0, 4), "-01-01")) < 0 && days.Count > 366 ? last[..4] : (object)new JsonArray(start, last);
        return new JsonObject
        {
            ["tooltip"] = new JsonObject { ["formatter"] = "{c}" },
            ["visualMap"] = new JsonObject
            {
                ["min"] = days.Min(d => d.Value),
                ["max"] = Math.Max(days.Max(d => d.Value), days.Min(d => d.Value) + 1),
                ["orient"] = "horizontal",
                ["left"] = "center",
                ["bottom"] = 0,
                ["textStyle"] = new JsonObject { ["color"] = t.Text },
                ["inRange"] = new JsonObject { ["color"] = new JsonArray("#e6f4f4", "#62c9c7", "#176b73") },
            },
            ["calendar"] = new JsonObject
            {
                ["range"] = range is string text ? JsonValue.Create(text) : (JsonNode)range,
                ["top"] = 40,
                ["left"] = 40,
                ["right"] = 16,
                ["cellSize"] = new JsonArray("auto", 16),
                ["itemStyle"] = new JsonObject { ["color"] = t.Surface, ["borderColor"] = t.Line },
                ["dayLabel"] = new JsonObject { ["color"] = t.Text },
                ["monthLabel"] = new JsonObject { ["color"] = t.Text },
                ["yearLabel"] = new JsonObject { ["color"] = t.Text },
            },
            ["series"] = new JsonArray(new JsonObject
            {
                ["type"] = "heatmap",
                ["coordinateSystem"] = "calendar",
                ["data"] = new JsonArray([.. days.Select(d => (JsonNode?)new JsonArray(d.Day, d.Value))]),
            }),
        };
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Data shaping
    // ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Categories in order of appearance with one series per measure, or one per value of the series column (sums repeated cells).</summary>
    private static (List<string> Categories, List<(string Name, List<double?> Values)> Series) Pivot(QueryResult result, Cols cols, bool allowSplit)
    {
        var x = cols.X ?? 0;
        var rows = result.Rows;
        if (allowSplit && cols.Series is { } s && cols.Y.Count > 0)
        {
            var m = cols.Y[0];
            var categories = rows.Select(r => Text(r[x])).Distinct().ToList();
            var names = rows.Select(r => Text(r[s])).Distinct().Take(MaxSeries).ToList();
            var series = names.Select(n => (Name: n, Values: categories.Select(c => (double?)null).ToList())).ToList();
            foreach (var row in rows)
            {
                var si = names.IndexOf(Text(row[s]));
                if (si < 0 || Number(row[m]) is not { } v)
                {
                    continue;
                }

                var ci = categories.IndexOf(Text(row[x]));
                series[si].Values[ci] = (series[si].Values[ci] ?? 0) + v;
            }

            return (categories, series);
        }

        var cats = rows.Select(r => Text(r[x])).ToList();
        var measures = cols.Y.Take(MaxSeries).ToList();
        return (cats, [.. measures.Select(i => (result.Columns[i].Name, rows.Select(r => Number(r[i])).ToList()))]);
    }

    private sealed record Slice(string Name, double Value);

    /// <summary>One slice per category with its summed value; beyond <see cref="MaxSlices"/> the smallest are grouped as "Other".</summary>
    private static List<Slice> Slices(QueryResult result, Cols cols)
    {
        var x = cols.X ?? 0;
        var m = cols.Y.Count > 0 ? cols.Y[0] : 1;
        var all = result.Rows
            .Select(r => (Name: Text(r[x]), Value: Number(r[m]) ?? 0))
            .Where(p => p.Value > 0)
            .GroupBy(p => p.Name)
            .Select(g => new Slice(g.Key, g.Sum(p => p.Value)))
            .OrderByDescending(s => s.Value)
            .ToList();
        if (all.Count <= MaxSlices)
        {
            return all;
        }

        var top = all.Take(MaxSlices - 1).ToList();
        top.Add(new Slice("Other", all.Skip(MaxSlices - 1).Sum(s => s.Value)));
        return top;
    }

    /// <summary>Name/value nodes: one level, or two when a second text column gives each category its parts.</summary>
    private static JsonArray Hierarchy(QueryResult result, Cols cols)
    {
        var x = cols.X ?? 0;
        var m = cols.Y.Count > 0 ? cols.Y[0] : 1;
        if (cols.Series is { } s)
        {
            return new JsonArray([.. result.Rows
                .Select(r => (Parent: Text(r[x]), Child: Text(r[s]), Value: Number(r[m]) ?? 0))
                .Where(p => p.Value > 0)
                .GroupBy(p => p.Parent)
                .Select(g => (JsonNode?)new JsonObject
                {
                    ["name"] = g.Key,
                    ["children"] = new JsonArray([.. g.GroupBy(p => p.Child).Select(c => (JsonNode?)new JsonObject { ["name"] = c.Key, ["value"] = c.Sum(p => p.Value) })]),
                })]);
        }

        return new JsonArray([.. Slices(result, cols).Select(sl => (JsonNode?)new JsonObject { ["name"] = sl.Name, ["value"] = sl.Value })]);
    }

    /// <summary>Removes edges that would close a loop (a Sankey must be acyclic), keeping the larger flows first.</summary>
    private static List<(string From, string To, double Value)> DropCycles(List<(string From, string To, double Value)> edges)
    {
        var kept = new List<(string From, string To, double Value)>();
        foreach (var edge in edges.OrderByDescending(e => e.Value))
        {
            if (!Reaches(kept, edge.To, edge.From))
            {
                kept.Add(edge);
            }
        }

        return kept;

        static bool Reaches(List<(string From, string To, double Value)> graph, string start, string goal)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>([start]);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node == goal)
                {
                    return true;
                }

                if (!seen.Add(node))
                {
                    continue;
                }

                foreach (var next in graph.Where(e => e.From == node))
                {
                    stack.Push(next.To);
                }
            }

            return false;
        }
    }

    /// <summary>The value at fraction <paramref name="q"/> (0 to 1) of an ascending list, interpolating between neighbours.</summary>
    public static double Quantile(List<double> sorted, double q)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var position = (sorted.Count - 1) * q;
        var below = (int)Math.Floor(position);
        var above = Math.Min(sorted.Count - 1, below + 1);
        return sorted[below] + ((sorted[above] - sorted[below]) * (position - below));
    }

    private static double Max(QueryResult result, int column)
    {
        var max = result.Rows.Select(r => Number(r[column]) ?? 0).DefaultIfEmpty(1).Max();
        return max <= 0 ? 1 : max * 1.1;
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------------------------------------------------------------------

    private sealed record Theme(bool Dark)
    {
        public string Text => Dark ? "#cbd5e1" : "#475569";

        public string Line => Dark ? "#334155" : "#dce7ea";

        public string Surface => Dark ? "#1e293b" : "#ffffff";
    }

    private static JsonObject Legend(Theme t) => new() { ["top"] = 0, ["type"] = "scroll", ["textStyle"] = new JsonObject { ["color"] = t.Text } };

    private static JsonObject Line(Theme t) => new() { ["lineStyle"] = new JsonObject { ["color"] = t.Line } };

    private static JsonObject Label(Theme t, int count) => new() { ["color"] = t.Text, ["hideOverlap"] = true, ["rotate"] = count > 12 ? 40 : 0 };

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    private static JsonArray Numbers(IEnumerable<double?> values) => new([.. values.Select(v => v is { } d ? JsonValue.Create(d) : null)]);

    private static string Format(double value) => Math.Abs(value) >= 1000 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Text(object? value) => value switch
    {
        null => "—",
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "—",
        JsonElement { ValueKind: JsonValueKind.Null } => "—",
        JsonElement e => e.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—",
    };

    /// <summary>A date as <c>yyyy-MM-dd</c> (what ECharts time and calendar axes read).</summary>
    private static string Date(object? value)
    {
        var text = Text(value);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : text;
    }

    private static double? Number(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetDouble(out var d) => d,
        JsonElement => null,
        IConvertible c => TryNumber(c),
        _ => null,
    };

    private static double? TryNumber(IConvertible c)
    {
        try
        {
            return Convert.ToDouble(c, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }
}
