using System.Globalization;
using System.Text;

namespace InsightFlow.Infrastructure.Connectors;

/// <summary>Where a sheet's table really starts: the first data row (1-based), the header rows above it, and the column names they spell out.</summary>
public sealed record SheetLayout(int FirstDataRow, int HeaderRows, int FirstHeaderRow, IReadOnlyList<string> ColumnNames)
{
    public string Describe() =>
        HeaderRows == 1
            ? $"column names on row {FirstHeaderRow}, data from row {FirstDataRow}"
            : $"column names built from rows {FirstHeaderRow}–{FirstDataRow - 1}, data from row {FirstDataRow}";
}

/// <summary>
/// Finds the real table in a sheet that does not start with a single header row: a title or report date on top, and column names spread over
/// two or more rows (a group row such as “Dealer Mapping” above the names). Works on the first rows only, as text, so it is pure and cheap.
/// Returns <c>null</c> when the sheet already has the plain layout (names on the first row), so ordinary sheets are read exactly as before.
/// </summary>
public static class SheetLayoutDetector
{
    public const int PreviewRows = 30;
    private const int MaxHeaderRows = 4;

    public static SheetLayout? Detect(IReadOnlyList<string?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count < 3)
        {
            return null;
        }

        var width = rows.Max(r => r.Length);
        var filled = rows.Select(r => r.Count(c => !string.IsNullOrWhiteSpace(c))).ToArray();
        var peak = filled.Max();
        if (peak < 2)
        {
            return null;
        }

        // The first row that looks like data: wide, and a good share of its cells are numbers.
        var data = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (IsDataRow(rows[i], filled[i], peak))
            {
                data = i;
                break;
            }
        }

        // Row 1 being data means no header at all; no data row at all means all-text sheet. Both keep DuckDB's default reading.
        if (data <= 0 || filled[data - 1] < peak * 0.5)
        {
            return null;
        }

        // Header rows: the names row right above the data, plus the rows above it that are wide enough to be group rows (not a title line).
        var first = data - 1;
        while (first > 0 && data - first < MaxHeaderRows && filled[first - 1] >= Math.Max(3, peak * 0.15))
        {
            first--;
        }

        var headerRows = data - first;
        if (first == 0 && headerRows == 1)
        {
            return null; // the ordinary layout
        }

        var names = BuildNames(rows, first, data - 1, width);
        return new SheetLayout(data + 1, headerRows, first + 1, names);
    }

    private static bool IsDataRow(string?[] row, int filled, int peak)
    {
        if (filled < peak * 0.5)
        {
            return false;
        }

        var numbers = row.Count(c => !string.IsNullOrWhiteSpace(c) && double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        return numbers >= filled * 0.25;
    }

    /// <summary>One name per column from the header rows. Group cells are carried to the right over blanks (merged cells), and only added to a name that would otherwise repeat.</summary>
    internal static IReadOnlyList<string> BuildNames(IReadOnlyList<string?[]> rows, int first, int last, int width)
    {
        var levels = new List<string[]>();
        for (var r = first; r <= last; r++)
        {
            var cells = new string[width];
            var carry = string.Empty;
            for (var c = 0; c < width; c++)
            {
                var text = c < rows[r].Length ? Tidy(rows[r][c]) : string.Empty;
                if (text.Length > 0)
                {
                    carry = text;
                }

                // Only the group rows (above the names row) carry over blanks; the names row itself does not.
                cells[c] = r < last ? carry : text;
            }

            levels.Add(cells);
        }

        var leaf = new string[width];
        var groups = new string[width];
        for (var c = 0; c < width; c++)
        {
            var parts = levels.Select(l => l[c]).Where(p => p.Length > 0).ToList();
            leaf[c] = parts.Count > 0 ? parts[^1] : string.Empty;
            groups[c] = string.Join(' ', parts.Take(Math.Max(0, parts.Count - 1)));
        }

        var counts = leaf.GroupBy(Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var names = new string[width];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var c = 0; c < width; c++)
        {
            var text = leaf[c];
            if (text.Length > 0 && counts[Key(text)] > 1 && groups[c].Length > 0)
            {
                text = $"{groups[c]} {text}";
            }

            var name = Snake(text);
            if (name.Length == 0)
            {
                name = $"column_{c + 1}";
            }

            var unique = name;
            for (var i = 2; !used.Add(unique); i++)
            {
                unique = $"{name}_{i}";
            }

            names[c] = unique;
        }

        return names;
    }

    private static string Key(string text) => Snake(text);

    private static string Tidy(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Lower-case letters, digits and underscores, the way DuckDB's own name normalisation writes names.</summary>
    internal static string Snake(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');
        }

        var name = string.Join('_', sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 80)
        {
            name = name[..80].TrimEnd('_');
        }

        return name.Length > 0 && char.IsAsciiDigit(name[0]) ? "c_" + name : name;
    }
}
