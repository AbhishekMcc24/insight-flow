using System.Globalization;
using InsightFlow.Domain.Viz;

namespace InsightFlow.Query.Dialects;

/// <summary>ANSI-flavoured defaults shared by the dialects; each engine overrides only what differs.</summary>
public abstract class SqlDialectBase : IQueryDialect
{
    public abstract string Name { get; }

    public virtual string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public virtual string Parameter(string name) => "$" + name;

    public string DateTrunc(TimeUnit unit, string expression, bool dateOnly)
    {
        var truncated = Truncate(unit, expression);
        return dateOnly && TruncateReturnsTimestamp ? $"CAST({truncated} AS DATE)" : truncated;
    }

    /// <summary>The engine's truncation expression.</summary>
    protected abstract string Truncate(TimeUnit unit, string expression);

    /// <summary>True when truncating a DATE yields a TIMESTAMP (DuckDB, PostgreSQL), so date-only columns are cast back.</summary>
    protected virtual bool TruncateReturnsTimestamp => true;

    public virtual string Aggregate(Agg agg, string expression) => agg switch
    {
        Agg.None => expression,
        Agg.Sum => $"SUM({expression})",
        Agg.Avg => $"AVG({expression})",
        Agg.Count => $"COUNT({expression})",
        Agg.CountDistinct => $"COUNT(DISTINCT {expression})",
        Agg.Min => $"MIN({expression})",
        Agg.Max => $"MAX({expression})",
        Agg.Median => Median(expression),
        _ => throw new NotSupportedException($"Aggregation {agg} is not supported."),
    };

    public virtual string ApplyLimit(string selectSql, int limit) =>
        string.Create(CultureInfo.InvariantCulture, $"{selectSql}\nLIMIT {limit}");

    protected virtual string Median(string expression) =>
        throw new NotSupportedException($"MEDIAN is not supported by the {Name} dialect yet.");

    /// <summary>Lower-case unit name for <c>date_trunc</c>-style functions.</summary>
    protected static string UnitName(TimeUnit unit) => unit switch
    {
        TimeUnit.Year => "year",
        TimeUnit.Quarter => "quarter",
        TimeUnit.Month => "month",
        TimeUnit.Week => "week",
        TimeUnit.Day => "day",
        TimeUnit.Hour => "hour",
        _ => throw new NotSupportedException($"Time unit {unit} is not supported."),
    };
}
