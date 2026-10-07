using InsightFlow.Domain.Viz;

namespace InsightFlow.Query.Dialects;

/// <summary>
/// The SQL differences between engines, isolated so <see cref="Compilation.SqlCompiler"/> stays engine-agnostic.
/// DuckDB is the execution engine in v1 (D6); the other dialects exist so live pushdown to source databases can be
/// added later without touching the compiler. Identifiers passed here always come from the semantic model —
/// never from user or model text — and every literal is a parameter.
/// </summary>
public interface IQueryDialect
{
    /// <summary>Stable dialect name (e.g. <c>duckdb</c>); part of the query cache key.</summary>
    string Name { get; }

    /// <summary>Quotes an identifier, escaping embedded quote characters.</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>The placeholder for a named parameter (e.g. <c>$p0</c>, <c>@p0</c>, <c>:p0</c>).</summary>
    string Parameter(string name);

    /// <summary>Truncates a date/time expression to <paramref name="unit"/>; <paramref name="dateOnly"/> keeps DATE columns typed as DATE.</summary>
    string DateTrunc(TimeUnit unit, string expression, bool dateOnly);

    /// <summary>Wraps an expression in an aggregate. Throws <see cref="NotSupportedException"/> when the engine lacks it.</summary>
    string Aggregate(Agg agg, string expression);

    /// <summary>Limits a complete SELECT statement to <paramref name="limit"/> rows.</summary>
    string ApplyLimit(string selectSql, int limit);
}
