using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Viz;

namespace InsightFlow.Web.Charts;

/// <summary>
/// Turns a <see cref="VizSpec"/> plus the rows the query engine returned for it into a Vega-Lite v6 specification.
/// Pure and deterministic (golden-tested): result columns are matched to channels by <see cref="QueryColumn.Channel"/>,
/// data is inlined, rows keep the SQL order (the compiler's ORDER BY is the sort), and the theme mirrors the Tailwind tokens.
/// <para>Supported marks: bar, line, area, point, pie (arc), heatmap (rect). <see cref="Mark.Table"/> is rendered by
/// <c>DataTable</c> instead. TODO(dev2): facets, size/label channels, box plot, density, selections and cross-filtering.</para>
/// </summary>
public static class VegaLiteSpecBuilder
{
    public const string SchemaUrl = "https://vega.github.io/schema/vega-lite/v6.json";

    /// <summary>Categorical palette (Tailwind-like hues) used for the color channel.</summary>
    public static readonly IReadOnlyList<string> Palette =
        ["#3b6ee0", "#f28e2b", "#2fa37c", "#e15759", "#8e6ad8", "#d4a72c", "#4fb3d9", "#b07aa1", "#ff9da7", "#9c755f"];

    public static bool Supports(Mark mark) => mark != Mark.Table;

    public static string BuildJson(VizSpec spec, QueryResult result, bool dark = false) =>
        Build(spec, result, dark).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static JsonObject Build(VizSpec spec, QueryResult result, bool dark = false)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(result);
        if (!Supports(spec.Mark))
        {
            throw new NotSupportedException($"Mark '{spec.Mark}' is not rendered with Vega-Lite.");
        }

        var columns = result.Columns.Where(c => c.Channel is not null).ToDictionary(c => c.Channel!, StringComparer.Ordinal);
        var root = new JsonObject
        {
            ["$schema"] = SchemaUrl,
            ["width"] = "container",
            ["height"] = "container",
            ["autosize"] = new JsonObject { ["type"] = "fit", ["contains"] = "padding" },
            ["background"] = "transparent",
            ["data"] = new JsonObject { ["values"] = Values(result) },
            ["mark"] = MarkDefinition(spec.Mark),
            ["encoding"] = Encoding(spec, columns),
            ["config"] = Theme(dark),
        };
        return root;
    }

    private static JsonObject MarkDefinition(Mark mark) => mark switch
    {
        Mark.Bar => new JsonObject { ["type"] = "bar", ["tooltip"] = true, ["cornerRadiusEnd"] = 2 },
        Mark.Line => new JsonObject { ["type"] = "line", ["tooltip"] = true, ["point"] = true, ["interpolate"] = "monotone" },
        Mark.Area => new JsonObject { ["type"] = "area", ["tooltip"] = true, ["line"] = true, ["opacity"] = 0.85 },
        Mark.Point => new JsonObject { ["type"] = "point", ["tooltip"] = true, ["filled"] = true, ["size"] = 60 },
        Mark.Pie => new JsonObject { ["type"] = "arc", ["tooltip"] = true, ["innerRadius"] = 0 },
        Mark.Heatmap => new JsonObject { ["type"] = "rect", ["tooltip"] = true },
        _ => throw new NotSupportedException($"Mark '{mark}' is not supported."),
    };

    private static JsonObject Encoding(VizSpec spec, Dictionary<string, QueryColumn> columns)
    {
        var encoding = new JsonObject();
        var x = Channel(spec.Encoding.X, columns, "x");
        var y = Channel(spec.Encoding.Y, columns, "y");
        var color = Channel(spec.Encoding.Color, columns, "color");

        if (spec.Mark == Mark.Pie)
        {
            // A pie is "slices by category (x), angle by measure (y)".
            if (y is not null)
            {
                encoding["theta"] = Field(y, forceType: "quantitative", Guide.None);
                encoding["theta"]!["stack"] = true;
            }

            if ((color ?? x) is { } slices)
            {
                encoding["color"] = Field(slices, forceType: "nominal", Guide.Legend);
                encoding["color"]!["sort"] = null;
            }

            encoding["tooltip"] = Tooltip(x, y, color);
            return encoding;
        }

        if (x is not null)
        {
            encoding["x"] = Field(x, forceType: spec.Mark == Mark.Heatmap && x.Type == "quantitative" ? "ordinal" : null, Guide.Axis);
        }

        if (y is not null)
        {
            encoding["y"] = Field(y, forceType: spec.Mark == Mark.Heatmap && y.Type == "quantitative" ? "ordinal" : null, Guide.Axis);
        }

        if (color is not null)
        {
            encoding["color"] = Field(color, forceType: null, Guide.Legend);
            if (spec.Mark == Mark.Heatmap)
            {
                encoding["color"]!["scale"] = new JsonObject { ["scheme"] = "blues" };
            }
        }

        encoding["tooltip"] = Tooltip(x, y, color);
        return encoding;
    }

    private sealed record ChannelInfo(string Column, string Type, string Title, TimeUnit? TimeUnit);

    private static ChannelInfo? Channel(FieldRef? field, Dictionary<string, QueryColumn> columns, string channel)
    {
        if (field is null || !columns.TryGetValue(channel, out var column))
        {
            return null;
        }

        var type = column.Type switch
        {
            ColumnType.Integer or ColumnType.Number => "quantitative",
            ColumnType.Date or ColumnType.DateTime => "temporal",
            _ => "nominal",
        };
        return new ChannelInfo(column.Name, type, Title(field), field.TimeUnit);
    }

    private enum Guide { None, Axis, Legend }

    private static JsonObject Field(ChannelInfo info, string? forceType, Guide guide)
    {
        var type = forceType ?? info.Type;
        var node = new JsonObject
        {
            ["field"] = info.Column,
            ["type"] = type,
            ["title"] = info.Title,
        };

        if (type == "temporal" && info.TimeUnit is { } unit)
        {
            node["timeUnit"] = VegaTimeUnit(unit);
        }

        if (type is "nominal" or "ordinal")
        {
            // Rows arrive in the compiler's ORDER BY order; keep it instead of Vega-Lite's alphabetical default.
            node["sort"] = null;
        }

        if (guide == Guide.Axis && type == "nominal")
        {
            node["axis"] = new JsonObject { ["labelLimit"] = 160 };
        }

        if (type == "quantitative" && guide != Guide.None)
        {
            node[guide == Guide.Axis ? "axis" : "legend"] = new JsonObject { ["format"] = "~s" };
        }

        return node;
    }

    private static JsonArray Tooltip(params ChannelInfo?[] channels)
    {
        var tooltip = new JsonArray();
        foreach (var c in channels.OfType<ChannelInfo>())
        {
            var item = new JsonObject { ["field"] = c.Column, ["type"] = c.Type, ["title"] = c.Title };
            if (c.Type == "quantitative")
            {
                item["format"] = ",.2~f";
            }
            else if (c.Type == "temporal" && c.TimeUnit is { } unit)
            {
                item["timeUnit"] = VegaTimeUnit(unit);
            }

            tooltip.Add(item);
        }

        return tooltip;
    }

    /// <summary>UTC variants: the engine returns truncated dates as ISO strings, which Vega parses as UTC.</summary>
    private static string VegaTimeUnit(TimeUnit unit) => unit switch
    {
        TimeUnit.Year => "utcyear",
        TimeUnit.Quarter => "utcyearquarter",
        TimeUnit.Month => "utcyearmonth",
        TimeUnit.Week => "utcyearweek",
        TimeUnit.Day => "utcyearmonthdate",
        TimeUnit.Hour => "utcyearmonthdatehours",
        _ => "utcyearmonthdate",
    };

    /// <summary>"orders.unit_price" + Avg → "Unit price (avg)".</summary>
    public static string Title(FieldRef field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var name = field.Field[(field.Field.LastIndexOf('.') + 1)..].Replace('_', ' ').Trim();
        if (name.Length > 0)
        {
            name = char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..];
        }

        var agg = field.Agg switch
        {
            Agg.None => null,
            Agg.CountDistinct => "distinct count",
            Agg.Avg => "avg",
            _ => field.Agg.ToString().ToLowerInvariant(),
        };
        var unit = field.TimeUnit?.ToString().ToLowerInvariant();
        var suffix = string.Join(", ", new[] { agg, unit }.OfType<string>());
        return suffix.Length == 0 ? name : $"{name} ({suffix})";
    }

    private static JsonArray Values(QueryResult result)
    {
        var values = new JsonArray();
        foreach (var row in result.Rows)
        {
            var obj = new JsonObject();
            for (var i = 0; i < result.Columns.Count && i < row.Count; i++)
            {
                obj[result.Columns[i].Name] = ToNode(row[i]);
            }

            values.Add(obj);
        }

        return values;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => double.IsFinite(d) ? JsonValue.Create(d) : null,
        decimal m => JsonValue.Create(m),
        float f => float.IsFinite(f) ? JsonValue.Create(f) : null,
        DateOnly date => JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        DateTime dt => JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static JsonObject Theme(bool dark)
    {
        var text = dark ? "#cbd5e1" : "#475569";   // slate-300 / slate-600
        var grid = dark ? "#1e293b" : "#e2e8f0";   // slate-800 / slate-200
        var palette = new JsonArray(Palette.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        return new JsonObject
        {
            ["font"] = "Inter, ui-sans-serif, system-ui, sans-serif",
            ["view"] = new JsonObject { ["stroke"] = null },
            ["range"] = new JsonObject { ["category"] = palette },
            ["mark"] = new JsonObject { ["color"] = Palette[0] },
            ["axis"] = new JsonObject
            {
                ["labelColor"] = text,
                ["titleColor"] = text,
                ["gridColor"] = grid,
                ["domainColor"] = grid,
                ["tickColor"] = grid,
                ["labelFontSize"] = 11,
                ["titleFontSize"] = 12,
                ["titleFontWeight"] = 500,
            },
            ["legend"] = new JsonObject { ["labelColor"] = text, ["titleColor"] = text },
        };
    }
}
