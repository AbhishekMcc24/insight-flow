using InsightFlow.Contracts.Query;
using InsightFlow.Evals;

namespace InsightFlow.Agents.Tests;

/// <summary>The eval scorer: answers are judged by values, tolerant of naming/column order, strict about content.</summary>
public sealed class ResultSetComparerTests
{
    private static QueryResult Result(string[] columns, params object?[][] rows) =>
        new(columns.Select(c => new QueryColumn(c, ColumnType.Other)).ToList(), rows.Select(r => (IReadOnlyList<object?>)r).ToList(), false);

    private static readonly QueryResult Expected = Result(["region", "revenue"], ["North", 100.0m], ["South", 250.5m]);

    [Fact]
    public void Compare_RenamedReorderedColumnsAndExtraColumn_Matches()
    {
        var actual = Result(["total_sales", "share", "Region"], [250.5, 0.7, "south"], [100L, 0.3, "NORTH "]);

        ResultSetComparer.Compare(Expected, actual, ordered: false, tolerance: 0.001).Match.ShouldBeTrue();
    }

    [Fact]
    public void Compare_WrongNumber_Fails()
    {
        var actual = Result(["region", "revenue"], ["North", 100.0m], ["South", 260m]);

        var outcome = ResultSetComparer.Compare(Expected, actual, ordered: false, tolerance: 0.001);

        outcome.Match.ShouldBeFalse();
    }

    [Fact]
    public void Compare_WithinTolerance_Matches_OutsideFails()
    {
        var close = Result(["r", "v"], ["North", 100.05m], ["South", 250.5m]);
        var far = Result(["r", "v"], ["North", 101m], ["South", 250.5m]);

        ResultSetComparer.Compare(Expected, close, false, 0.001).Match.ShouldBeTrue();
        ResultSetComparer.Compare(Expected, far, false, 0.001).Match.ShouldBeFalse();
    }

    [Fact]
    public void Compare_Ordered_RequiresSameOrder()
    {
        var swapped = Result(["region", "revenue"], ["South", 250.5m], ["North", 100.0m]);

        ResultSetComparer.Compare(Expected, swapped, ordered: false, tolerance: 0.001).Match.ShouldBeTrue();
        ResultSetComparer.Compare(Expected, swapped, ordered: true, tolerance: 0.001).Match.ShouldBeFalse();
    }

    [Fact]
    public void Compare_DifferentRowCount_FailsWithReason()
    {
        var outcome = ResultSetComparer.Compare(Expected, Result(["region", "revenue"], ["North", 100.0m]), false, 0.001);

        outcome.Match.ShouldBeFalse();
        outcome.Reason.ShouldContain("expected 2 row(s)");
    }

    [Fact]
    public void Compare_DatesAndTimestamps_MatchByCalendarValue()
    {
        var expected = Result(["m", "v"], [new DateOnly(2024, 1, 1), 1L]);
        var actual = Result(["month", "value"], [new DateTime(2024, 1, 1), 1.0]);

        ResultSetComparer.Compare(expected, actual, true, 0.001).Match.ShouldBeTrue();
    }

    [Fact]
    public void Compare_NullsMatchOnlyNulls()
    {
        ResultSetComparer.ValueEquals(null, null, 0.001).ShouldBeTrue();
        ResultSetComparer.ValueEquals(null, 0L, 0.001).ShouldBeFalse();
    }
}
