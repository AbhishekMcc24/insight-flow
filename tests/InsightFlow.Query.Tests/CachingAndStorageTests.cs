using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Viz;
using InsightFlow.Query.Caching;
using InsightFlow.Query.Compilation;
using InsightFlow.Query.Modeling;
using InsightFlow.Query.Storage;
using InsightFlow.Testing;

namespace InsightFlow.Query.Tests;

public sealed class CachingAndStorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 30, 0, TimeSpan.Zero);

    private static VizSpec Spec(params FilterSpec[] filters) =>
        new(1, RetailModel.SalesVersionId, Mark.Bar, new Encoding(new FieldRef("channel"), new FieldRef("revenue", Agg.Sum)), filters);

    [Fact]
    public void CacheKey_FilterOrder_DoesNotMatter()
    {
        FilterSpec a = new EqualsFilter("channel", "Online");
        FilterSpec b = new RangeFilter("revenue", Min: 1);

        var k1 = QueryCacheKey.Create(RetailModel.Tenant, Spec(a, b), RetailModel.Create(), "duckdb", Now);
        var k2 = QueryCacheKey.Create(RetailModel.Tenant, Spec(b, a), RetailModel.Create(), "duckdb", Now);

        k1.ShouldBe(k2);
    }

    [Fact]
    public void CacheKey_DiffersByTenantModelSpecAndDialect()
    {
        var model = RetailModel.Create();
        var baseKey = QueryCacheKey.Create(RetailModel.Tenant, Spec(), model, "duckdb", Now);

        QueryCacheKey.Create(RetailModel.OtherTenant, Spec(), model, "duckdb", Now).ShouldNotBe(baseKey);
        QueryCacheKey.Create(RetailModel.Tenant, Spec(new EqualsFilter("channel", "Store")), model, "duckdb", Now).ShouldNotBe(baseKey);
        QueryCacheKey.Create(RetailModel.Tenant, Spec(), model with { Measures = [] }, "duckdb", Now).ShouldNotBe(baseKey);
        QueryCacheKey.Create(RetailModel.Tenant, Spec(), model, "postgres", Now).ShouldNotBe(baseKey);
    }

    [Fact]
    public void CacheKey_RelativeDates_RollWithTheHour_OthersDoNot()
    {
        var model = RetailModel.Create();
        var relative = Spec(new RelativeDateFilter("order_date", TimeUnit.Day, RelativeDateAnchor.Last, 7));

        QueryCacheKey.Create(RetailModel.Tenant, relative, model, "duckdb", Now)
            .ShouldNotBe(QueryCacheKey.Create(RetailModel.Tenant, relative, model, "duckdb", Now.AddHours(1)));
        QueryCacheKey.Create(RetailModel.Tenant, Spec(), model, "duckdb", Now)
            .ShouldBe(QueryCacheKey.Create(RetailModel.Tenant, Spec(), model, "duckdb", Now.AddDays(3)));
    }

    [Fact]
    public void CacheKey_ContainsNoTenantOrSpecText()
    {
        var key = QueryCacheKey.Create(RetailModel.Tenant, Spec(new EqualsFilter("channel", "secret-value")), RetailModel.Create(), "duckdb", Now);

        key.Value.ShouldStartWith("insightflow:query:v1:");
        key.Value.ShouldNotContain("secret-value");
        key.Value.ShouldNotContain(RetailModel.Tenant.ToString());
    }

    [Theory]
    [InlineData(TimeUnit.Month, RelativeDateAnchor.Last, 3, "2026-07-01T00:00:00", "2026-10-01T00:00:00")]
    [InlineData(TimeUnit.Quarter, RelativeDateAnchor.Current, 1, "2026-10-01T00:00:00", "2027-01-01T00:00:00")]
    [InlineData(TimeUnit.Week, RelativeDateAnchor.Current, 1, "2026-10-05T00:00:00", "2026-10-12T00:00:00")]
    [InlineData(TimeUnit.Day, RelativeDateAnchor.Next, 2, "2026-10-08T00:00:00", "2026-10-10T00:00:00")]
    [InlineData(TimeUnit.Year, RelativeDateAnchor.ToDate, 1, "2026-01-01T00:00:00", "2026-10-07T12:30:00")]
    [InlineData(TimeUnit.Hour, RelativeDateAnchor.Last, 6, "2026-10-07T06:00:00", "2026-10-07T12:00:00")]
    public void RelativeDateRange_ComputesHalfOpenUtcBounds(TimeUnit unit, RelativeDateAnchor anchor, int count, string start, string end)
    {
        var (s, e) = RelativeDateRange.Compute(new RelativeDateFilter("d", unit, anchor, count), Now);

        s.ToString("s", System.Globalization.CultureInfo.InvariantCulture).ShouldBe(start);
        e.ToString("s", System.Globalization.CultureInfo.InvariantCulture).ShouldBe(end);
    }

    [Fact]
    public void ImplicitModel_NumericColumnsAreMeasures()
    {
        var version = DatasetVersion.CreateSource(Guid.NewGuid(), RetailModel.Tenant, RetailExtractFixture.SalesSchema, 3, "t", Now);

        var model = ImplicitSemanticModel.For(version);

        model.Tables.ShouldHaveSingleItem().SourceDatasetVersionId.ShouldBe(version.Id);
        model.Tables[0].Columns.Single(c => c.Name == "revenue").Role.ShouldBe(ColumnRole.Measure);
        model.Tables[0].Columns.Single(c => c.Name == "channel").Role.ShouldBe(ColumnRole.Dimension);
    }

    [Fact]
    public async Task LocalCache_EvictsLeastRecentlyUsed_AndDedupesConcurrentFills()
    {
        var root = Path.Combine(Path.GetTempPath(), "insightflow-cache-tests", Guid.NewGuid().ToString("N"));
        var cache = new LocalExtractCache(root, maxBytes: 2_500);
        var fills = 0;
        async Task Fill(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref fills);
            await Task.Delay(50, ct);
            await File.WriteAllBytesAsync(path, new byte[1_000], ct);
        }

        try
        {
            var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

            // Ten concurrent requests for the same missing file share one fill.
            await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => cache.GetOrAddAsync(RetailModel.Tenant, ids[0], Fill, TestContext.Current.CancellationToken)));
            fills.ShouldBe(1);

            await cache.GetOrAddAsync(RetailModel.Tenant, ids[1], Fill, TestContext.Current.CancellationToken);
            File.SetLastAccessTimeUtc(cache.PathFor(RetailModel.Tenant, ids[0]), DateTime.UtcNow.AddHours(-1));
            File.SetLastAccessTimeUtc(cache.PathFor(RetailModel.Tenant, ids[1]), DateTime.UtcNow);

            // Third file pushes the total to 3,000 bytes > 2,500: the least recently used (ids[0]) goes.
            await cache.GetOrAddAsync(RetailModel.Tenant, ids[2], Fill, TestContext.Current.CancellationToken);

            File.Exists(cache.PathFor(RetailModel.Tenant, ids[0])).ShouldBeFalse();
            File.Exists(cache.PathFor(RetailModel.Tenant, ids[1])).ShouldBeTrue();
            File.Exists(cache.PathFor(RetailModel.Tenant, ids[2])).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LocalCache_PathsAreBuiltFromIdsOnly()
    {
        var cache = new LocalExtractCache(Path.Combine(Path.GetTempPath(), "insightflow-cache-tests"), 1 << 20);
        var id = Guid.NewGuid();

        var path = cache.PathFor(RetailModel.Tenant, id);

        Path.GetFileName(path).ShouldBe($"{id:N}.parquet");
        path.ShouldStartWith(cache.Root);
    }
}
