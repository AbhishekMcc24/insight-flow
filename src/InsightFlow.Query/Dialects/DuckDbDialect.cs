using InsightFlow.Domain.Viz;

namespace InsightFlow.Query.Dialects;

/// <summary>DuckDB, the v1 execution engine over Parquet extracts (fully implemented).</summary>
public sealed class DuckDbDialect : SqlDialectBase
{
    public static DuckDbDialect Instance { get; } = new();

    public override string Name => "duckdb";

    /// <summary>
    /// <c>date_trunc</c> returns a TIMESTAMP even for DATE input (verified on DuckDB 1.5), so the base class casts
    /// date-only columns back to DATE. Weeks start on Monday (ISO 8601).
    /// </summary>
    protected override string Truncate(TimeUnit unit, string expression) => $"date_trunc('{UnitName(unit)}', {expression})";

    protected override string Median(string expression) => $"MEDIAN({expression})";
}
