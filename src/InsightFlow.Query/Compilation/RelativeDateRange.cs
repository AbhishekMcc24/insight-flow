using InsightFlow.Domain.Viz;

namespace InsightFlow.Query.Compilation;

/// <summary>
/// Turns a relative date filter into a concrete half-open UTC range <c>[Start, End)</c> at compile time. Computing
/// bounds in .NET (instead of engine date functions) keeps the SQL identical across dialects and makes it testable
/// with a fixed clock. Weeks start on Monday (ISO 8601).
/// </summary>
public static class RelativeDateRange
{
    public static (DateTime Start, DateTime End) Compute(RelativeDateFilter filter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var utcNow = now.UtcDateTime;
        var currentStart = StartOf(filter.Unit, utcNow);

        return filter.Anchor switch
        {
            RelativeDateAnchor.Current => (currentStart, Add(filter.Unit, currentStart, 1)),
            RelativeDateAnchor.Last => (Add(filter.Unit, currentStart, -filter.Count), currentStart),
            RelativeDateAnchor.Next => (Add(filter.Unit, currentStart, 1), Add(filter.Unit, currentStart, filter.Count + 1)),
            RelativeDateAnchor.ToDate => (currentStart, utcNow),
            _ => throw new NotSupportedException($"Anchor {filter.Anchor} is not supported."),
        };
    }

    public static DateTime StartOf(TimeUnit unit, DateTime t) => unit switch
    {
        TimeUnit.Year => new DateTime(t.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        TimeUnit.Quarter => new DateTime(t.Year, ((t.Month - 1) / 3 * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc),
        TimeUnit.Month => new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        TimeUnit.Week => t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7)),
        TimeUnit.Day => t.Date,
        TimeUnit.Hour => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc),
        _ => throw new NotSupportedException($"Time unit {unit} is not supported."),
    };

    public static DateTime Add(TimeUnit unit, DateTime t, int count) => unit switch
    {
        TimeUnit.Year => t.AddYears(count),
        TimeUnit.Quarter => t.AddMonths(3 * count),
        TimeUnit.Month => t.AddMonths(count),
        TimeUnit.Week => t.AddDays(7 * count),
        TimeUnit.Day => t.AddDays(count),
        TimeUnit.Hour => t.AddHours(count),
        _ => throw new NotSupportedException($"Time unit {unit} is not supported."),
    };
}
