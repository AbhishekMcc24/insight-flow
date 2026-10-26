using InsightFlow.Contracts.Workspace;

using System.Globalization;
using System.Text.Json.Nodes;

namespace InsightFlow.Web.Charts;

/// <summary>
/// Apache ECharts options for the Home dashboard's statistics charts. These are fixed, tiny charts over aggregates
/// (not user-defined <c>VizSpec</c>s), so they are built directly. Colours come from <see cref="EChartsOptionBuilder.Palette"/>
/// so dashboard and explorer charts look like one product.
/// </summary>
public static class DashboardCharts
{
    private static string Text(bool dark) => dark ? "#cbd5e1" : "#475569";

    private static string Grid(bool dark) => dark ? "#1e293b" : "#dce7ea";

    /// <summary>Documents uploaded per day as a smooth filled line.</summary>
    public static string UploadsOverTime(IReadOnlyList<DailyCountDto> days, bool dark) =>
        new JsonObject
        {
            ["color"] = new JsonArray(EChartsOptionBuilder.Palette[0]),
            ["grid"] = new JsonObject { ["left"] = 8, ["right"] = 16, ["top"] = 16, ["bottom"] = 8, ["containLabel"] = true },
            ["tooltip"] = new JsonObject { ["trigger"] = "axis" },
            ["xAxis"] = new JsonObject
            {
                ["type"] = "category",
                ["boundaryGap"] = false,
                ["data"] = new JsonArray(days.Select(d => (JsonNode?)d.Day.ToString("d MMM", CultureInfo.InvariantCulture)).ToArray()),
                ["axisLabel"] = new JsonObject { ["color"] = Text(dark), ["hideOverlap"] = true },
                ["axisLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = Grid(dark) } },
            },
            ["yAxis"] = new JsonObject
            {
                ["type"] = "value",
                ["minInterval"] = 1,
                ["axisLabel"] = new JsonObject { ["color"] = Text(dark) },
                ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = Grid(dark) } },
            },
            ["series"] = new JsonArray(new JsonObject
            {
                ["name"] = "Documents",
                ["type"] = "line",
                ["smooth"] = true,
                ["showSymbol"] = false,
                ["data"] = new JsonArray(days.Select(d => (JsonNode?)d.Count).ToArray()),
                ["lineStyle"] = new JsonObject { ["width"] = 3 },
                ["areaStyle"] = new JsonObject { ["opacity"] = 0.16 },
            }),
        }.ToJsonString();

    /// <summary>Share of documents per file type as a donut.</summary>
    public static string DocumentsByType(IReadOnlyList<FileTypeStatDto> types, bool dark) =>
        new JsonObject
        {
            ["color"] = new JsonArray(EChartsOptionBuilder.Palette.Select(c => (JsonNode?)c).ToArray()),
            ["tooltip"] = new JsonObject { ["trigger"] = "item", ["formatter"] = "{b}: {c} ({d}%)" },
            ["legend"] = new JsonObject { ["bottom"] = 0, ["textStyle"] = new JsonObject { ["color"] = Text(dark) } },
            ["series"] = new JsonArray(new JsonObject
            {
                ["name"] = "File type",
                ["type"] = "pie",
                ["radius"] = new JsonArray("48%", "72%"),
                ["center"] = new JsonArray("50%", "44%"),
                ["avoidLabelOverlap"] = true,
                ["itemStyle"] = new JsonObject { ["borderColor"] = dark ? "#0f172a" : "#ffffff", ["borderWidth"] = 2, ["borderRadius"] = 6 },
                ["label"] = new JsonObject { ["show"] = false },
                ["data"] = new JsonArray(types.Select(t => (JsonNode?)new JsonObject { ["name"] = t.Type.ToUpperInvariant(), ["value"] = t.Count }).ToArray()),
            }),
        }.ToJsonString();

    /// <summary>Documents per folder as horizontal bars (largest on top).</summary>
    public static string DocumentsByFolder(IReadOnlyList<FolderStatDto> folders, bool dark)
    {
        var ordered = folders.Reverse().ToList();
        return new JsonObject
        {
            ["color"] = new JsonArray(EChartsOptionBuilder.Palette[0]),
            ["grid"] = new JsonObject { ["left"] = 8, ["right"] = 24, ["top"] = 8, ["bottom"] = 8, ["containLabel"] = true },
            ["tooltip"] = new JsonObject { ["trigger"] = "axis", ["axisPointer"] = new JsonObject { ["type"] = "shadow" } },
            ["xAxis"] = new JsonObject
            {
                ["type"] = "value",
                ["minInterval"] = 1,
                ["axisLabel"] = new JsonObject { ["color"] = Text(dark) },
                ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = Grid(dark) } },
            },
            ["yAxis"] = new JsonObject
            {
                ["type"] = "category",
                ["data"] = new JsonArray(ordered.Select(f => (JsonNode?)f.Folder).ToArray()),
                ["axisLabel"] = new JsonObject { ["color"] = Text(dark), ["width"] = 110, ["overflow"] = "truncate" },
                ["axisLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = Grid(dark) } },
            },
            ["series"] = new JsonArray(new JsonObject
            {
                ["name"] = "Documents",
                ["type"] = "bar",
                ["barMaxWidth"] = 22,
                ["itemStyle"] = new JsonObject { ["borderRadius"] = new JsonArray(0, 6, 6, 0) },
                ["data"] = new JsonArray(ordered.Select(f => (JsonNode?)f.Count).ToArray()),
            }),
        }.ToJsonString();
    }

    /// <summary>Extract runs by outcome as vertical bars, using colour plus the category label (never colour alone).</summary>
    public static string ExtractOutcomes(int succeeded, int failed, int inProgress, bool dark) =>
        new JsonObject
        {
            ["grid"] = new JsonObject { ["left"] = 8, ["right"] = 16, ["top"] = 16, ["bottom"] = 8, ["containLabel"] = true },
            ["tooltip"] = new JsonObject { ["trigger"] = "axis", ["axisPointer"] = new JsonObject { ["type"] = "shadow" } },
            ["xAxis"] = new JsonObject
            {
                ["type"] = "category",
                ["data"] = new JsonArray("Succeeded", "Failed", "In progress"),
                ["axisLabel"] = new JsonObject { ["color"] = Text(dark) },
                ["axisLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = Grid(dark) } },
            },
            ["yAxis"] = new JsonObject
            {
                ["type"] = "value",
                ["minInterval"] = 1,
                ["axisLabel"] = new JsonObject { ["color"] = Text(dark) },
                ["splitLine"] = new JsonObject { ["lineStyle"] = new JsonObject { ["color"] = Grid(dark) } },
            },
            ["series"] = new JsonArray(new JsonObject
            {
                ["name"] = "Extract runs",
                ["type"] = "bar",
                ["barMaxWidth"] = 48,
                ["label"] = new JsonObject { ["show"] = true, ["position"] = "top", ["color"] = Text(dark) },
                ["itemStyle"] = new JsonObject { ["borderRadius"] = new JsonArray(6, 6, 0, 0) },
                ["data"] = new JsonArray(
                    new JsonObject { ["value"] = succeeded, ["itemStyle"] = new JsonObject { ["color"] = "#16856b" } },
                    new JsonObject { ["value"] = failed, ["itemStyle"] = new JsonObject { ["color"] = "#dc5353" } },
                    new JsonObject { ["value"] = inProgress, ["itemStyle"] = new JsonObject { ["color"] = "#e8963a" } }),
            }),
        }.ToJsonString();
}
