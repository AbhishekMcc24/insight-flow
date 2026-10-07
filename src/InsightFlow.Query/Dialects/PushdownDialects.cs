using System.Globalization;
using InsightFlow.Domain.Viz;

namespace InsightFlow.Query.Dialects;

// TODO(roy): live pushdown. These dialects compile VizSpecs for direct execution on source databases. They are covered
// by golden tests but no executor uses them yet (v1 ingests everything to Parquet and queries DuckDB, D6/D13).
// Before enabling pushdown: verify week semantics per engine, timezone handling of timestamps, and the semantic-model
// measure expressions (written in DuckDB SQL) — which need per-dialect translation or must be rejected.
// Known gap: MySQL 8 does not support LIMIT inside an IN (...) subquery, so Top-N must compile to a derived-table join there.

/// <summary>PostgreSQL (planned live pushdown).</summary>
public sealed class PostgresDialect : SqlDialectBase
{
    public static PostgresDialect Instance { get; } = new();

    public override string Name => "postgres";

    public override string Parameter(string name) => "@" + name;

    protected override string Truncate(TimeUnit unit, string expression) => $"date_trunc('{UnitName(unit)}', {expression})";

    protected override string Median(string expression) => $"percentile_cont(0.5) WITHIN GROUP (ORDER BY {expression})";
}

/// <summary>SQL Server 2022+ (planned live pushdown). Uses <c>DATETRUNC</c> and <c>OFFSET … FETCH</c>.</summary>
public sealed class SqlServerDialect : SqlDialectBase
{
    public static SqlServerDialect Instance { get; } = new();

    public override string Name => "sqlserver";

    public override string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    public override string Parameter(string name) => "@" + name;

    protected override string Truncate(TimeUnit unit, string expression) => $"DATETRUNC({UnitName(unit)}, {expression})";

    /// <summary>DATETRUNC preserves the input type.</summary>
    protected override bool TruncateReturnsTimestamp => false;

    /// <summary>The compiler always emits ORDER BY for limited queries, which OFFSET/FETCH requires.</summary>
    public override string ApplyLimit(string selectSql, int limit) =>
        string.Create(CultureInfo.InvariantCulture, $"{selectSql}\nOFFSET 0 ROWS FETCH NEXT {limit} ROWS ONLY");
}

/// <summary>MySQL 8 (planned live pushdown). MySQL has no <c>date_trunc</c>; units are rebuilt from date parts.</summary>
public sealed class MySqlDialect : SqlDialectBase
{
    public static MySqlDialect Instance { get; } = new();

    public override string Name => "mysql";

    public override string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        return "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    public override string Parameter(string name) => "@" + name;

    protected override string Truncate(TimeUnit unit, string expression) => unit switch
    {
        TimeUnit.Year => $"MAKEDATE(YEAR({expression}), 1)",
        TimeUnit.Quarter => $"MAKEDATE(YEAR({expression}), 1) + INTERVAL (QUARTER({expression}) - 1) QUARTER",
        TimeUnit.Month => $"DATE_FORMAT({expression}, '%Y-%m-01')",
        TimeUnit.Week => $"DATE_SUB(DATE({expression}), INTERVAL WEEKDAY({expression}) DAY)",
        TimeUnit.Day => $"DATE({expression})",
        TimeUnit.Hour => $"DATE_FORMAT({expression}, '%Y-%m-%d %H:00:00')",
        _ => throw new NotSupportedException($"Time unit {unit} is not supported."),
    };

    /// <summary>The expressions above already yield dates for date-level units.</summary>
    protected override bool TruncateReturnsTimestamp => false;
}

/// <summary>Oracle 19c+ (planned live pushdown). Uses <c>TRUNC</c> format models and <c>FETCH FIRST</c>.</summary>
public sealed class OracleDialect : SqlDialectBase
{
    public static OracleDialect Instance { get; } = new();

    public override string Name => "oracle";

    public override string Parameter(string name) => ":" + name;

    protected override string Truncate(TimeUnit unit, string expression) => unit switch
    {
        TimeUnit.Year => $"TRUNC({expression}, 'YYYY')",
        TimeUnit.Quarter => $"TRUNC({expression}, 'Q')",
        TimeUnit.Month => $"TRUNC({expression}, 'MM')",
        TimeUnit.Week => $"TRUNC({expression}, 'IW')",
        TimeUnit.Day => $"TRUNC({expression}, 'DD')",
        TimeUnit.Hour => $"TRUNC({expression}, 'HH24')",
        _ => throw new NotSupportedException($"Time unit {unit} is not supported."),
    };

    public override string ApplyLimit(string selectSql, int limit) =>
        string.Create(CultureInfo.InvariantCulture, $"{selectSql}\nFETCH FIRST {limit} ROWS ONLY");

    /// <summary>TRUNC on a DATE returns a DATE.</summary>
    protected override bool TruncateReturnsTimestamp => false;

    protected override string Median(string expression) => $"MEDIAN({expression})";
}
