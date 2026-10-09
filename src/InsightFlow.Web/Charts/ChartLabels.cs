using System.Globalization;
using System.Text.Json;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Viz;

namespace InsightFlow.Web.Charts;

/// <summary>
/// Human-readable text for charts and tables: field titles ("Unit price (avg)") and category labels for result cells,
/// including time-unit buckets ("2025 Q1", "Jan 2025"). Pure and culture-invariant so chart specs stay deterministic.
/// </summary>
public static class ChartLabels
{
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
            _ => field.Agg.ToString().ToLowerInvariant(),
        };
        var unit = field.TimeUnit?.ToString().ToLowerInvariant();
        var suffix = string.Join(", ", new[] { agg, unit }.OfType<string>());
        return suffix.Length == 0 ? name : $"{name} ({suffix})";
    }

    /// <summary>Category label of a cell. Dates truncated to a time unit get a unit-shaped label.</summary>
    public static string Category(object? value, ColumnType type, TimeUnit? unit)
    {
        var text = Text(value);
        if (text is null)
        {
            return "(blank)";
        }

        if (type is ColumnType.Date or ColumnType.DateTime && TryParseDate(text, out var date))
        {
            return unit switch
            {
                TimeUnit.Year => date.ToString("yyyy", CultureInfo.InvariantCulture),
                TimeUnit.Quarter => $"{date.Year.ToString(CultureInfo.InvariantCulture)} Q{(date.Month + 2) / 3}",
                TimeUnit.Month => date.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                TimeUnit.Week => $"{ISOWeek.GetYear(date).ToString(CultureInfo.InvariantCulture)}-W{ISOWeek.GetWeekOfYear(date):00}",
                TimeUnit.Hour => date.ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture),
                _ when type == ColumnType.Date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            };
        }

        return text;
    }

    /// <summary>Numeric value of a cell, or null when it is missing or not a finite number.</summary>
    public static double? Number(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetDouble(out var d) && double.IsFinite(d) => d,
        JsonElement { ValueKind: JsonValueKind.String } e when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        JsonElement { ValueKind: JsonValueKind.True } => 1,
        JsonElement { ValueKind: JsonValueKind.False } => 0,
        JsonElement => null,
        double d => double.IsFinite(d) ? d : null,
        float f => float.IsFinite(f) ? f : null,
        IConvertible c and not string and not bool => c.ToDouble(CultureInfo.InvariantCulture),
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>Invariant text of a cell (null for missing values).</summary>
    public static string? Text(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.True } => "true",
        JsonElement { ValueKind: JsonValueKind.False } => "false",
        JsonElement e => e.GetRawText(),
        bool b => b ? "true" : "false",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static bool TryParseDate(string text, out DateTime date) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date);
}
