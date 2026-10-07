using BenchmarkDotNet.Attributes;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Viz;
using InsightFlow.Query;
using InsightFlow.Query.Compilation;
using InsightFlow.Query.Dialects;
using InsightFlow.Query.Execution;
using InsightFlow.Query.Storage;
using InsightFlow.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace InsightFlow.Benchmarks;

/// <summary>Representative chart specs used by both benchmark classes.</summary>
internal static class BenchmarkSpecs
{
    public static readonly VizSpec RevenueByRegion = new(1, RetailModel.SalesVersionId, Mark.Bar,
        new Encoding(new FieldRef("region"), new FieldRef("revenue", Agg.Sum)), []);

    public static readonly VizSpec MonthlyRevenueByChannel = new(1, RetailModel.SalesVersionId, Mark.Line,
        new Encoding(new FieldRef("order_date", TimeUnit: TimeUnit.Month), new FieldRef("revenue", Agg.Sum), Color: new FieldRef("channel")), []);

    public static readonly VizSpec Top10ProductsInGermany = new(1, RetailModel.SalesVersionId, Mark.Bar,
        new Encoding(new FieldRef("product_name"), new FieldRef("revenue", Agg.Sum)),
        [new EqualsFilter("country", "Germany"), new TopNFilter("product_name", 10, new FieldRef("revenue", Agg.Sum))]);
}

/// <summary>VizSpec → SQL compile latency (no I/O).</summary>
[MemoryDiagnoser]
public class CompilerBenchmarks
{
    private readonly SqlCompiler _compiler = new(new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero)));
    private readonly SemanticModel _model = RetailModel.Create();

    [Benchmark]
    public CompiledQuery CompileSingleTable() => _compiler.Compile(BenchmarkSpecs.MonthlyRevenueByChannel, _model, DuckDbDialect.Instance);

    [Benchmark]
    public CompiledQuery CompileWithJoin() => _compiler.Compile(BenchmarkSpecs.RevenueByRegion, _model, DuckDbDialect.Instance);

    [Benchmark]
    public CompiledQuery CompileTopNWithJoins() => _compiler.Compile(BenchmarkSpecs.Top10ProductsInGermany, _model, DuckDbDialect.Instance);
}

/// <summary>
/// End-to-end compile + DuckDB execution over 10,000,000 sales rows (Parquet on local disk, warm OS cache).
/// The dataset is generated once into <c>%TEMP%/insightflow-bench/10m</c> and reused across runs.
/// </summary>
[MemoryDiagnoser]
public class DuckDbGroupByBenchmarks
{
    public const int SalesRows = 10_000_000;

    private readonly SqlCompiler _compiler = new(TimeProvider.System);
    private readonly SemanticModel _model = RetailModel.Create();
    private DuckDbQueryExecutor _executor = null!;
    private IReadOnlyList<DatasetVersion> _versions = [];

    [GlobalSetup]
    public void Setup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "insightflow-bench", "10m");
        var files = new RetailDataGenerator.NormalizedFiles(
            Path.Combine(dir, "sales.parquet"), Path.Combine(dir, "products.parquet"), Path.Combine(dir, "stores.parquet"));
        if (!File.Exists(files.Sales))
        {
            Console.WriteLine($"Generating {SalesRows:N0} rows into {dir} (one-time)...");
            files = RetailDataGenerator.WriteNormalizedParquetAsync(dir, SalesRows).GetAwaiter().GetResult();
        }

        var created = DateTimeOffset.UtcNow;
        _versions =
        [
            DatasetVersion.CreateSource(RetailModel.SalesVersionId, RetailModel.Tenant, DatasetSchema.Empty, SalesRows, "bench", created),
            DatasetVersion.CreateSource(RetailModel.ProductsVersionId, RetailModel.Tenant, DatasetSchema.Empty, RetailDataGenerator.ProductCount, "bench", created),
            DatasetVersion.CreateSource(RetailModel.StoresVersionId, RetailModel.Tenant, DatasetSchema.Empty, RetailDataGenerator.StoreCount, "bench", created),
        ];
        var store = new LocalFilesExtractStore(dir, new Dictionary<Guid, string>
        {
            [RetailModel.SalesVersionId] = files.Sales,
            [RetailModel.ProductsVersionId] = files.Products,
            [RetailModel.StoresVersionId] = files.Stores,
        });
        _executor = new DuckDbQueryExecutor(
            store,
            Options.Create(new QueryEngineOptions { QueryTimeout = TimeSpan.FromMinutes(2), MemoryLimit = "4GB" }),
            NullLogger<DuckDbQueryExecutor>.Instance);
    }

    [Benchmark]
    public Task<QueryResult> SumRevenueByRegion_Join() => RunAsync(BenchmarkSpecs.RevenueByRegion);

    [Benchmark]
    public Task<QueryResult> MonthlyRevenueByChannel_DateTrunc() => RunAsync(BenchmarkSpecs.MonthlyRevenueByChannel);

    [Benchmark]
    public Task<QueryResult> Top10ProductsInGermany_TopNTwoJoins() => RunAsync(BenchmarkSpecs.Top10ProductsInGermany);

    private Task<QueryResult> RunAsync(VizSpec spec) =>
        _executor.ExecuteAsync(_compiler.Compile(spec, _model, DuckDbDialect.Instance), _versions, CancellationToken.None);

    private sealed class LocalFilesExtractStore(string root, IReadOnlyDictionary<Guid, string> files) : IExtractStore
    {
        public string LocalRoot => root;

        public Task<string> GetLocalPathAsync(DatasetVersion version, CancellationToken cancellationToken) => Task.FromResult(files[version.Id]);

        public Task<Uri> SaveAsync(TenantId tenant, Guid datasetVersionId, Stream parquet, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
