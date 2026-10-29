using System.Text.RegularExpressions;
using InsightFlow.Domain.Contracts.Query;

namespace InsightFlow.Domain.Contracts.Playground;

/// <summary>How an element looks. Colours are <c>#rgb</c>/<c>#rrggbb</c> or <c>transparent</c>; an empty value means "the default for this kind of element".</summary>
public sealed record PlaygroundStyleDto(
    string? Fill = null,
    string? Stroke = null,
    double StrokeWidth = 1,
    string? Color = null,
    double FontSize = 14,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    string Align = "left",
    string VAlign = "middle",
    double Radius = 0,
    double Opacity = 1);

/// <summary>
/// One thing on a sheet's canvas. <see cref="Type"/> is one of <see cref="PlaygroundElementTypes.All"/>; <see cref="X"/>, <see cref="Y"/>,
/// <see cref="W"/> and <see cref="H"/> are in canvas pixels. Charts and tables carry a copy of the rows they were made from (<see cref="Data"/>), so
/// they keep showing the numbers they were exported with. A table can keep its header row (<see cref="FreezeHeader"/>) and its first columns
/// (<see cref="FreezeColumns"/>) in view while the rest scrolls.
/// </summary>
public sealed record PlaygroundElementDto(
    Guid Id,
    string Type,
    double X,
    double Y,
    double W,
    double H,
    int Z = 0,
    string? Text = null,
    PlaygroundStyleDto? Style = null,
    ChartSpecDto? Chart = null,
    QueryResult? Data = null,
    string? Url = null,
    bool Locked = false,
    bool Flip = false,
    bool FreezeHeader = true,
    int FreezeColumns = 0);

public sealed record PlaygroundSheetSummaryDto(Guid Id, string Name, int Sequence, int ElementCount, DateTimeOffset UpdatedAt);

public sealed record PlaygroundSheetDto(Guid Id, string Name, int Sequence, IReadOnlyList<PlaygroundElementDto> Elements, DateTimeOffset UpdatedAt);

public sealed record CreatePlaygroundSheetRequest(string? Name = null);

/// <summary>Saves a sheet: any part left null is kept as it is.</summary>
public sealed record SavePlaygroundSheetRequest(string? Name = null, IReadOnlyList<PlaygroundElementDto>? Elements = null, int? Sequence = null);

/// <summary>
/// Adds a chart or table made elsewhere (Query, Workspace) to a sheet. With no <see cref="SheetId"/> and no sheets yet, a sheet is created; with
/// <see cref="NewSheetName"/> a new sheet is created for it.
/// </summary>
public sealed record ExportToPlaygroundRequest(PlaygroundElementDto Element, Guid? SheetId = null, string? NewSheetName = null);

public sealed record ExportToPlaygroundResult(Guid SheetId, string SheetName, bool SheetCreated);

/// <summary>The kinds of element a sheet can hold: the pieces of an Excel "Insert" menu that make sense on a free canvas.</summary>
public static class PlaygroundElementTypes
{
    public static readonly IReadOnlyList<string> All =
    [
        "text", "title", "note", "link",
        "rect", "rounded", "ellipse", "triangle", "diamond", "hexagon", "star",
        "line", "arrow",
        "image", "chart", "table", "kpi",
    ];

    public static bool IsKnown(string? type) => type is not null && All.Contains(type, StringComparer.Ordinal);
}

/// <summary>
/// Checks and tidies the elements a browser sends before they are stored, so a sheet can never hold unknown kinds of element, scripts in a link,
/// runaway sizes or colours that are not colours. Anything unusable is dropped or reset, never trusted. Pure, so it is unit-tested.
/// </summary>
public static partial class PlaygroundElementRules
{
    public const int MaxElements = 300;
    public const int MaxTextLength = 5_000;
    public const int MaxUrlLength = 1_500_000;
    public const int MaxDataRows = 500;
    public const int MaxDataColumns = 60;

    [GeneratedRegex(@"^(#[0-9a-fA-F]{3,8}|transparent)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ColourPattern();

    [GeneratedRegex(@"^data:image/(png|jpe?g|gif|webp|svg\+xml);base64,[A-Za-z0-9+/=]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DataImage();

    public static IReadOnlyList<PlaygroundElementDto> Sanitize(IReadOnlyList<PlaygroundElementDto>? elements)
    {
        if (elements is null)
        {
            return [];
        }

        var seen = new HashSet<Guid>();
        var result = new List<PlaygroundElementDto>();
        foreach (var e in elements.Take(MaxElements))
        {
            if (e is null || !PlaygroundElementTypes.IsKnown(e.Type))
            {
                continue;
            }

            var id = e.Id == Guid.Empty || !seen.Add(e.Id) ? Guid.CreateVersion7() : e.Id;
            seen.Add(id);
            var s = e.Style ?? new PlaygroundStyleDto();
            result.Add(new PlaygroundElementDto(
                id,
                e.Type,
                Clamp(e.X, -5_000, 20_000, 0),
                Clamp(e.Y, -5_000, 20_000, 0),
                Clamp(e.W, 1, 5_000, 120),
                Clamp(e.H, 1, 5_000, 60),
                Math.Clamp(e.Z, -10_000, 10_000),
                Cut(e.Text, MaxTextLength),
                new PlaygroundStyleDto(
                    Colour(s.Fill),
                    Colour(s.Stroke),
                    Clamp(s.StrokeWidth, 0, 40, 1),
                    Colour(s.Color),
                    Clamp(s.FontSize, 6, 200, 14),
                    s.Bold,
                    s.Italic,
                    s.Underline,
                    s.Align is "left" or "center" or "right" ? s.Align : "left",
                    s.VAlign is "top" or "middle" or "bottom" ? s.VAlign : "middle",
                    Clamp(s.Radius, 0, 500, 0),
                    Clamp(s.Opacity, 0, 1, 1)),
                e.Chart is { } chart && ChartTypes.Normalize(chart.Type) is { } type ? chart with { Type = type } : null,
                Trim(e.Data),
                SafeUrl(e.Url, e.Type),
                e.Locked,
                e.Flip,
                e.FreezeHeader,
                Math.Clamp(e.FreezeColumns, 0, 5)));
        }

        return result;
    }

    /// <summary>A link may be http, https or mailto; an image an http(s) address or an embedded png/jpeg/gif/webp/svg; anything else (javascript:, file:) is dropped.</summary>
    public static string? SafeUrl(string? url, string type)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength)
        {
            return null;
        }

        var value = url.Trim();
        try
        {
            if (type == "image" && value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return DataImage().IsMatch(value) ? value : null;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (value.Length > 2_048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" || (type == "link" && uri.Scheme == "mailto") ? uri.AbsoluteUri : null;
    }

    private static string? Colour(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return ColourPattern().IsMatch(value.Trim()) ? value.Trim() : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Clamp(value, min, max);

    private static string? Cut(string? text, int max) => string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;

    /// <summary>Keeps at most <see cref="MaxDataRows"/> rows and <see cref="MaxDataColumns"/> columns of the copy a chart or table carries.</summary>
    private static QueryResult? Trim(QueryResult? data)
    {
        if (data is null || data.Columns.Count == 0)
        {
            return null;
        }

        var columns = data.Columns.Take(MaxDataColumns).ToList();
        var rows = data.Rows.Take(MaxDataRows).Select(r => (IReadOnlyList<object?>)[.. r.Take(columns.Count)]).ToList();
        return new QueryResult(columns, rows, data.Truncated || data.Rows.Count > MaxDataRows);
    }
}
