using System.Globalization;
using System.Text;
using InsightFlow.Domain.Viz;
using InsightFlow.Query.Compilation;
using InsightFlow.Query.Dialects;
using InsightFlow.Testing;
using Microsoft.Extensions.Time.Testing;
using SortDirection = InsightFlow.Domain.Viz.SortDirection;

namespace InsightFlow.Query.Tests;

/// <summary>
/// Golden-file tests of the compiled SQL across a matrix of specs. Any change to generated SQL shows up as a reviewed
/// diff of <c>Golden/*.verified.sql</c>. The clock is fixed so relative-date parameters are stable.
/// </summary>
public sealed class CompilerGoldenTests
{
    internal static readonly DateTimeOffset FixedNow = new(2026, 10, 7, 12, 30, 0, TimeSpan.Zero);

    private static VizSpec Spec(Mark mark, VizEncoding encoding, IReadOnlyList<FilterSpec>? filters = null, IReadOnlyList<SortSpec>? sort = null, int limit = 5_000) =>
        new(VizSpec.CurrentSchemaVersion, RetailModel.SalesVersionId, mark, encoding, filters ?? [], sort, limit);

    /// <summary>Every case compiles in DuckDB; <c>Portable</c> cases also compile for the pushdown dialects.</summary>
    public static readonly IReadOnlyList<(string Name, bool Portable, VizSpec Spec)> Cases =
    [
        ("bar_sum_revenue_by_channel", true,
            Spec(Mark.Bar, new VizEncoding(new FieldRef("channel"), new FieldRef("revenue", Agg.Sum)))),
        ("line_monthly_revenue_by_region_join", true,
            Spec(Mark.Line, new VizEncoding(new FieldRef("order_date", TimeUnit: TimeUnit.Month), new FieldRef("revenue", Agg.Sum), Color: new FieldRef("region")))),
        ("heatmap_region_category_profit_two_joins", false,
            Spec(Mark.Heatmap, new VizEncoding(new FieldRef("stores.region"), new FieldRef("products.category"), Color: new FieldRef("profit")))),
        ("pie_units_by_category_with_label", true,
            Spec(Mark.Pie, new VizEncoding(null, new FieldRef("quantity", Agg.Sum), Color: new FieldRef("category"), Label: new FieldRef("category")))),
        ("table_raw_rows", true,
            Spec(Mark.Table, new VizEncoding(new FieldRef("order_id"), new FieldRef("channel"), Label: new FieldRef("revenue")), limit: 100)),
        ("filters_equals_in_range_null", true,
            Spec(Mark.Bar, new VizEncoding(new FieldRef("country"), new FieldRef("revenue", Agg.Sum)),
            [
                new EqualsFilter("channel", "Online"),
                new InFilter("country", ["Germany", "France"]),
                new RangeFilter("revenue", Min: 10, Max: 2_500.5m, MaxInclusive: false),
                new EqualsFilter("is_returned", ScalarValue.Null, Exclude: true),
                new EqualsFilter("store_name", "Store 001", Exclude: true),
            ])),
        ("in_filters_with_nulls", true,
            Spec(Mark.Bar, new VizEncoding(new FieldRef("region"), new FieldRef("order_id", Agg.Count)),
            [
                new InFilter("region", ["North", ScalarValue.Null]),
                new InFilter("category", ["Toys", ScalarValue.Null], Exclude: true),
                new InFilter("channel", ["Online"], Exclude: true),
            ])),
        ("relative_last_3_months_on_date", true,
            Spec(Mark.Bar, new VizEncoding(new FieldRef("order_date", TimeUnit: TimeUnit.Week), new FieldRef("revenue", Agg.Sum)),
            [new RelativeDateFilter("order_date", TimeUnit.Month, RelativeDateAnchor.Last, 3)])),
        ("relative_hour_to_date_on_timestamp", false,
            Spec(Mark.Line, new VizEncoding(new FieldRef("ordered_at", TimeUnit: TimeUnit.Hour), new FieldRef("quantity", Agg.Sum)),
            [new RelativeDateFilter("ordered_at", TimeUnit.Day, RelativeDateAnchor.ToDate)])),
        ("top5_products_by_revenue_online", true,
            Spec(Mark.Bar, new VizEncoding(new FieldRef("product_name"), new FieldRef("revenue", Agg.Sum)),
            [
                new EqualsFilter("channel", "Online"),
                new TopNFilter("product_name", 5, new FieldRef("revenue", Agg.Sum)),
            ],
            [new SortSpec("revenue", SortDirection.Desc)])),
        ("count_distinct_orders_by_quarter", true,
            Spec(Mark.Area, new VizEncoding(new FieldRef("order_date", TimeUnit: TimeUnit.Quarter), new FieldRef("order_id", Agg.CountDistinct)))),
        ("median_and_avg_price_by_category", false,
            Spec(Mark.Point, new VizEncoding(new FieldRef("revenue", Agg.Median), new FieldRef("revenue", Agg.Avg), Color: new FieldRef("category")))),
        ("scatter_raw_points_sized", true,
            Spec(Mark.Point, new VizEncoding(new FieldRef("revenue"), new FieldRef("cost"), Size: new FieldRef("quantity")), limit: 1_000)),
        ("single_total_kpi", true,
            Spec(Mark.Table, new VizEncoding(null, new FieldRef("revenue", Agg.Sum)))),
    ];

    public static readonly TheoryData<string> Dialects = new("duckdb", "postgres", "sqlserver", "mysql", "oracle");

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Compile_SpecMatrix_MatchesGolden(string dialectName)
    {
        IQueryDialect dialect = dialectName switch
        {
            "duckdb" => DuckDbDialect.Instance,
            "postgres" => PostgresDialect.Instance,
            "sqlserver" => SqlServerDialect.Instance,
            "mysql" => MySqlDialect.Instance,
            _ => OracleDialect.Instance,
        };
        var compiler = new SqlCompiler(new FakeTimeProvider(FixedNow));
        var model = RetailModel.Create();
        var output = new StringBuilder();

        foreach (var (name, portable, spec) in Cases.Where(c => dialect is DuckDbDialect || c.Portable))
        {
            var compiled = compiler.Compile(spec, model, dialect);
            output.Append("-- case: ").Append(name).Append('\n')
                .Append(compiled.Sql).Append(";\n")
                .Append("-- sources: ").Append(string.Join(", ", compiled.Sources.Select(s => $"{s.RelationName}={s.DatasetVersionId}"))).Append('\n')
                .Append("-- params: ").Append(compiled.Parameters.Count == 0 ? "(none)" : string.Join(", ", compiled.Parameters.Select(Format))).Append("\n\n");
        }

        Golden.Match(output.ToString(), dialectName, "sql");
    }

    [Fact]
    public void Compile_SameSpecTwice_IsDeterministic()
    {
        var compiler = new SqlCompiler(new FakeTimeProvider(FixedNow));
        var model = RetailModel.Create();

        foreach (var (_, _, spec) in Cases)
        {
            compiler.Compile(spec, model, DuckDbDialect.Instance).Sql.ShouldBe(compiler.Compile(spec, model, DuckDbDialect.Instance).Sql);
        }
    }

    [Fact]
    public void Compile_InvalidSpec_ThrowsWithAllErrors()
    {
        var compiler = new SqlCompiler(new FakeTimeProvider(FixedNow));
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("nope"), new FieldRef("channel", Agg.Sum)));

        var ex = Should.Throw<VizSpecValidationException>(() => compiler.Compile(spec, RetailModel.Create(), DuckDbDialect.Instance));

        ex.Errors.Select(e => e.Code).ShouldBe(["unknown_field", "illegal_aggregation"], ignoreOrder: true);
    }

    [Fact]
    public void Compile_UnconnectedTable_ReportsNoJoinPath()
    {
        var model = RetailModel.Create() with { Relationships = [] };
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("region"), new FieldRef("revenue", Agg.Sum)));

        var ex = Should.Throw<VizSpecValidationException>(() => new SqlCompiler(TimeProvider.System).Compile(spec, model, DuckDbDialect.Instance));

        ex.Errors.ShouldHaveSingleItem().Code.ShouldBe("no_join_path");
    }

    [Fact]
    public void Compile_HostileIdentifiers_AreQuotedNotInjected()
    {
        // Column names come from the model (i.e. from source data headers), so even those are never trusted.
        var model = RetailModel.Create();
        var sales = model.Tables[0];
        var hostile = sales with { Columns = [.. sales.Columns, new("x\"; DROP TABLE t; --", Domain.Modeling.DataType.String, Domain.Modeling.ColumnRole.Dimension)] };
        model = model with { Tables = [hostile, .. model.Tables.Skip(1)] };
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("x\"; DROP TABLE t; --"), new FieldRef("revenue", Agg.Sum)));

        var sql = new SqlCompiler(TimeProvider.System).Compile(spec, model, DuckDbDialect.Instance).Sql;

        sql.ShouldContain("t0.\"x\"\"; DROP TABLE t; --\"");
    }

    [Fact]
    public void Compile_FilterValues_AreParametersNeverSqlText()
    {
        var spec = Spec(Mark.Bar, new VizEncoding(new FieldRef("channel"), new FieldRef("revenue", Agg.Sum)),
            [new EqualsFilter("channel", "x' OR 1=1 --")]);

        var compiled = new SqlCompiler(TimeProvider.System).Compile(spec, RetailModel.Create(), DuckDbDialect.Instance);

        compiled.Sql.ShouldNotContain("OR 1=1");
        compiled.Parameters.ShouldHaveSingleItem().Value.ShouldBe("x' OR 1=1 --");
    }

    private static string Format(QueryParameter p) => p.Value switch
    {
        null => $"{p.Name} = NULL",
        string s => $"{p.Name} = '{s}' (String)",
        DateOnly d => $"{p.Name} = {d:yyyy-MM-dd} (DateOnly)",
        DateTime dt => $"{p.Name} = {dt:yyyy-MM-ddTHH:mm:ss} (DateTime)",
        IFormattable f => $"{p.Name} = {f.ToString(null, CultureInfo.InvariantCulture)} ({p.Value.GetType().Name})",
        _ => $"{p.Name} = {p.Value} ({p.Value.GetType().Name})",
    };
}
