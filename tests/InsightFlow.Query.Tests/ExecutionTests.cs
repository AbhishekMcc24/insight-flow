using DuckDB.NET.Data;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Viz;
using InsightFlow.Query.Caching;
using InsightFlow.Query.Compilation;
using InsightFlow.Query.Dialects;
using InsightFlow.Query.Execution;
using InsightFlow.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SortDirection = InsightFlow.Domain.Viz.SortDirection;

namespace InsightFlow.Query.Tests;

/// <summary>
/// Compiles specs and executes them on DuckDB over real Parquet, comparing results with independently written SQL.
/// </summary>
public sealed class ExecutionTests(RetailExtractFixture data) : IClassFixture<RetailExtractFixture>
{
    private static readonly FakeTimeProvider Clock = new(CompilerGoldenTests.FixedNow);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DuckDbQueryExecutor Executor(TimeSpan? timeout = null) => new(
        data.Store,
        Options.Create(new QueryEngineOptions { QueryTimeout = timeout ?? TimeSpan.FromSeconds(30), MemoryLimit = "1GB" }),
        NullLogger<DuckDbQueryExecutor>.Instance);

    private async Task<QueryResult> RunAsync(VizSpec spec)
    {
        var compiled = new SqlCompiler(Clock).Compile(spec, RetailModel.Create(), DuckDbDialect.Instance);
        return await Executor().ExecuteAsync(compiled, data.Versions, Ct);
    }

    private static VizSpec Spec(Encoding encoding, IReadOnlyList<FilterSpec>? filters = null, IReadOnlyList<SortSpec>? sort = null, int limit = 5_000, Mark mark = Mark.Bar) =>
        new(VizSpec.CurrentSchemaVersion, RetailModel.SalesVersionId, mark, encoding, filters ?? [], sort, limit);

    [Fact]
    public async Task SumRevenueByRegion_ThroughJoin_MatchesDirectSql()
    {
        var result = await RunAsync(Spec(new Encoding(new FieldRef("region"), new FieldRef("revenue", Agg.Sum))));
        var expected = await data.QueryDirectAsync(
            "SELECT st.region, SUM(s.revenue) FROM {sales} s LEFT JOIN {stores} st USING (store_id) GROUP BY 1 ORDER BY 1");

        result.Columns.Select(c => (c.Name, c.Type, c.Channel)).ShouldBe([("x", ColumnType.String, "x"), ("y", ColumnType.Number, "y")]);
        result.Rows.Select(r => (r[0], r[1])).ShouldBe(expected.Select(r => (r[0], r[1])));
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task MonthTimeUnit_ReturnsFirstDayOfMonthDates()
    {
        var result = await RunAsync(Spec(new Encoding(new FieldRef("order_date", TimeUnit: TimeUnit.Month), new FieldRef("order_id", Agg.Count))));

        result.Columns[0].Type.ShouldBe(ColumnType.Date);
        result.Rows.Count.ShouldBe(36); // 2023-01 .. 2025-12
        result.Rows.ShouldAllBe(r => ((DateOnly)r[0]!).Day == 1);
        result.Rows.Sum(r => (long)r[1]!).ShouldBe(RetailExtractFixture.SalesRows);
    }

    [Fact]
    public async Task TopNWithOtherFilter_ReturnsRankedSubset()
    {
        var spec = Spec(
            new Encoding(new FieldRef("product_name"), new FieldRef("revenue", Agg.Sum)),
            [new EqualsFilter("channel", "Online"), new TopNFilter("product_name", 5, new FieldRef("revenue", Agg.Sum))],
            [new SortSpec("revenue", SortDirection.Desc)]);

        var result = await RunAsync(spec);
        var expected = await data.QueryDirectAsync("""
            SELECT p.product_name, SUM(s.revenue) AS r FROM {sales} s JOIN {products} p USING (product_id)
            WHERE s.channel = 'Online' GROUP BY 1 ORDER BY r DESC, 1 LIMIT 5
            """);

        result.Rows.Select(r => r[0]).ShouldBe(expected.Select(r => r[0]));
        result.Rows.Select(r => (decimal)r[1]!).ShouldBeInOrder(Shouldly.SortDirection.Descending);
    }

    [Fact]
    public async Task Filters_AreAppliedWithParameters()
    {
        var spec = Spec(
            new Encoding(null, new FieldRef("order_id", Agg.Count)),
            [
                new InFilter("country", ["Germany", "France"]),
                new RangeFilter("quantity", Min: 3, Max: 7),
                new EqualsFilter("is_returned", false),
                new RangeFilter("order_date", Min: "2024-01-01", Max: "2024-12-31"),
            ],
            mark: Mark.Table);

        var result = await RunAsync(spec);
        var expected = await data.QueryDirectAsync("""
            SELECT COUNT(s.order_id) FROM {sales} s LEFT JOIN {stores} st USING (store_id)
            WHERE st.country IN ('Germany','France') AND s.quantity BETWEEN 3 AND 7 AND NOT s.is_returned
              AND s.order_date BETWEEN DATE '2024-01-01' AND DATE '2024-12-31'
            """);

        result.Rows.ShouldHaveSingleItem()[0].ShouldBe(expected[0][0]);
        ((long)result.Rows[0][0]!).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task RelativeDateFilter_UsesFixedClock()
    {
        // Clock: 2026-10-07; data ends 2025-12-31, so "last 12 months" (Oct 2025 – Sep 2026) covers Oct–Dec 2025.
        var spec = Spec(
            new Encoding(new FieldRef("order_date", TimeUnit: TimeUnit.Month), new FieldRef("order_id", Agg.Count)),
            [new RelativeDateFilter("order_date", TimeUnit.Month, RelativeDateAnchor.Last, 12)]);

        var result = await RunAsync(spec);

        result.Rows.Select(r => (DateOnly)r[0]!).ShouldBe([new DateOnly(2025, 10, 1), new DateOnly(2025, 11, 1), new DateOnly(2025, 12, 1)]);
    }

    [Fact]
    public async Task CalculatedMeasure_IsEvaluated()
    {
        var result = await RunAsync(Spec(new Encoding(new FieldRef("channel"), new FieldRef("profit"))));
        var expected = await data.QueryDirectAsync("SELECT channel, SUM(revenue) - SUM(cost) FROM {sales} GROUP BY 1 ORDER BY 1");

        result.Rows.Select(r => (r[0], r[1])).ShouldBe(expected.Select(r => (r[0], r[1])));
    }

    [Fact]
    public async Task RowLimit_ReportsTruncation()
    {
        var result = await RunAsync(Spec(new Encoding(new FieldRef("order_id"), new FieldRef("revenue")), limit: 10, mark: Mark.Point));

        result.Rows.Count.ShouldBe(10);
        result.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task Timeout_InterruptsLongRunningQuery()
    {
        var heavy = new CompiledQuery("duckdb", "SELECT count(*) FROM range(100000000000) a, range(10) b WHERE a.range % 7 = b.range", [], [], [], 10);
        var started = DateTime.UtcNow;

        await Should.ThrowAsync<TimeoutException>(() => Executor(TimeSpan.FromSeconds(1)).ExecuteAsync(heavy, [], Ct));

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CallerCancellation_InterruptsLongRunningQuery()
    {
        var heavy = new CompiledQuery("duckdb", "SELECT count(*) FROM range(100000000000) a, range(10) b WHERE a.range % 7 = b.range", [], [], [], 10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;

        await Should.ThrowAsync<OperationCanceledException>(() => Executor().ExecuteAsync(heavy, [], cts.Token));

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task LockedDownConnection_CannotReadOtherFiles()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"insightflow-outside-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(outside, "a\n1\n", Ct);
        try
        {
            var sneaky = new CompiledQuery("duckdb", $"SELECT * FROM read_csv('{outside.Replace('\\', '/')}')", [], [], [], 10);

            var ex = await Should.ThrowAsync<DuckDBException>(() => Executor().ExecuteAsync(sneaky, [], Ct));

            ex.Message.ShouldContain("disabled by configuration");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Engine_SecondRun_IsServedFromCache()
    {
        var cache = new InMemoryQueryCache();
        var engine = new VizQueryEngine(new SqlCompiler(Clock), Executor(), cache, Clock);
        var spec = Spec(new Encoding(new FieldRef("channel"), new FieldRef("quantity", Agg.Sum)));
        var model = RetailModel.Create();

        var first = await engine.RunAsync(RetailModel.Tenant, spec, model, data.Versions, Ct);
        var requestsAfterFirst = data.Store.Requests;
        var second = await engine.RunAsync(RetailModel.Tenant, spec, model, data.Versions, Ct);

        first.FromCache.ShouldBeFalse();
        second.FromCache.ShouldBeTrue();
        data.Store.Requests.ShouldBe(requestsAfterFirst); // no extract access on a hit
        second.Result.Rows.Count.ShouldBe(first.Result.Rows.Count);
    }

    [Fact]
    public async Task Engine_OtherTenantsVersions_AreRefused()
    {
        var engine = new VizQueryEngine(new SqlCompiler(Clock), Executor(), new InMemoryQueryCache(), Clock);
        var spec = Spec(new Encoding(new FieldRef("channel"), new FieldRef("quantity", Agg.Sum)));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            engine.RunAsync(RetailModel.OtherTenant, spec, RetailModel.Create(), data.Versions, Ct));
    }

    [Fact]
    public async Task Preview_ReturnsFirstRowsAndSchema()
    {
        var engine = new VizQueryEngine(new SqlCompiler(Clock), Executor(), new InMemoryQueryCache(), Clock);

        var result = await engine.PreviewAsync(data.Versions[0], 25, Ct);

        result.Rows.Count.ShouldBe(25);
        result.Truncated.ShouldBeTrue();
        result.Columns.Select(c => c.Name).ShouldContain("revenue");
        result.Columns.Single(c => c.Name == "order_date").Type.ShouldBe(ColumnType.Date);
    }

    private sealed class InMemoryQueryCache : IQueryCache
    {
        private readonly Dictionary<string, QueryResult> _entries = [];

        public Task<QueryResult?> GetAsync(QueryCacheKey key, CancellationToken cancellationToken) =>
            Task.FromResult(_entries.GetValueOrDefault(key.Value));

        public Task SetAsync(QueryCacheKey key, QueryResult result, CancellationToken cancellationToken)
        {
            _entries[key.Value] = result;
            return Task.CompletedTask;
        }
    }
}
