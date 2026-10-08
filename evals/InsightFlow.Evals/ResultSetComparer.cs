using System.Globalization;
using InsightFlow.Contracts.Query;

namespace InsightFlow.Evals;

/// <summary>
/// Compares an agent's result with the expected result by VALUES, not SQL or column names:
/// <list type="bullet">
/// <item>each expected column is mapped to an agent column whose values match (extra agent columns are ignored);</item>
/// <item>numbers match within a relative tolerance; text matches case-insensitively after trimming; dates/timestamps by calendar value;</item>
/// <item>rows are compared in order (rankings, time series) or as a multiset.</item>
/// </list>
/// </summary>
public static class ResultSetComparer
{
    public sealed record Outcome(bool Match, string Reason);

    public static Outcome Compare(QueryResult expected, QueryResult actual, bool ordered, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (expected.Rows.Count != actual.Rows.Count)
        {
            return new(false, $"expected {expected.Rows.Count} row(s), got {actual.Rows.Count}");
        }

        var mapping = new int[expected.Columns.Count];
        var used = new HashSet<int>();
        for (var e = 0; e < expected.Columns.Count; e++)
        {
            var match = Enumerable.Range(0, actual.Columns.Count)
                .Where(a => !used.Contains(a))
                .FirstOrDefault(a => ColumnMatches(expected, e, actual, a, ordered, tolerance), -1);
            if (match < 0)
            {
                return new(false, $"no column of the answer matches expected column {e + 1} ('{expected.Columns[e].Name}')");
            }

            mapping[e] = match;
            used.Add(match);
        }

        var expectedRows = expected.Rows.Select(r => r.ToArray()).ToList();
        var actualRows = actual.Rows.Select(r => mapping.Select(i => r[i]).ToArray()).ToList();

        if (ordered)
        {
            for (var i = 0; i < expectedRows.Count; i++)
            {
                if (!RowEquals(expectedRows[i], actualRows[i], tolerance))
                {
                    return new(false, $"row {i + 1} differs");
                }
            }

            return new(true, "match");
        }

        var remaining = new List<object?[]>(actualRows);
        foreach (var row in expectedRows)
        {
            var index = remaining.FindIndex(a => RowEquals(row, a, tolerance));
            if (index < 0)
            {
                return new(false, "a row of the expected result is missing from the answer");
            }

            remaining.RemoveAt(index);
        }

        return new(true, "match");
    }

    private static bool ColumnMatches(QueryResult expected, int e, QueryResult actual, int a, bool ordered, double tolerance)
    {
        var ev = expected.Rows.Select(r => r[e]).ToList();
        var av = actual.Rows.Select(r => r[a]).ToList();
        if (ordered)
        {
            return ev.Zip(av).All(p => ValueEquals(p.First, p.Second, tolerance));
        }

        var pool = new List<object?>(av);
        foreach (var value in ev)
        {
            var i = pool.FindIndex(x => ValueEquals(value, x, tolerance));
            if (i < 0)
            {
                return false;
            }

            pool.RemoveAt(i);
        }

        return true;
    }

    private static bool RowEquals(object?[] expected, object?[] actual, double tolerance) =>
        expected.Zip(actual).All(p => ValueEquals(p.First, p.Second, tolerance));

    internal static bool ValueEquals(object? expected, object? actual, double tolerance)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        if (TryNumber(expected, out var en) && TryNumber(actual, out var an))
        {
            var scale = Math.Max(Math.Abs(en), 1e-9);
            return Math.Abs(en - an) <= tolerance * scale || Math.Abs(en - an) < 1e-9;
        }

        if (TryDate(expected, out var ed) && TryDate(actual, out var ad))
        {
            return ed == ad;
        }

        return string.Equals(Text(expected), Text(actual), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNumber(object value, out double number)
    {
        switch (value)
        {
            case long l: number = l; return true;
            case int i: number = i; return true;
            case decimal d: number = (double)d; return true;
            case double dbl: number = dbl; return true;
            case bool: number = 0; return false;
            default: number = 0; return false;
        }
    }

    private static bool TryDate(object value, out DateTime date)
    {
        switch (value)
        {
            case DateOnly d:
                date = d.ToDateTime(TimeOnly.MinValue);
                return true;
            case DateTime dt:
                date = dt;
                return true;
            case string s when s.Length >= 10 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed):
                date = parsed;
                return true;
            default:
                date = default;
                return false;
        }
    }

    private static string Text(object value) => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Trim();
}
