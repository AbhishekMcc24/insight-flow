using DuckDB.NET.Data;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Query.Storage;
using InsightFlow.Testing;

namespace InsightFlow.Query.Tests;

/// <summary>Generates the retail Parquet files once per test class and exposes them through a fake extract store.</summary>
public sealed class RetailExtractFixture : IAsyncLifetime
{
    public const int SalesRows = 5_000;
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "insightflow-query-tests", Guid.NewGuid().ToString("N"));

    public RetailDataGenerator.NormalizedFiles Files { get; private set; } = null!;

    public IReadOnlyList<DatasetVersion> Versions { get; private set; } = [];

    public FakeExtractStore Store { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Files = await RetailDataGenerator.WriteNormalizedParquetAsync(Directory, SalesRows);
        Versions =
        [
            DatasetVersion.CreateSource(RetailModel.SalesVersionId, RetailModel.Tenant, DatasetSchema.Empty, SalesRows, "test", Created),
            DatasetVersion.CreateSource(RetailModel.ProductsVersionId, RetailModel.Tenant, DatasetSchema.Empty, RetailDataGenerator.ProductCount, "test", Created),
            DatasetVersion.CreateSource(RetailModel.StoresVersionId, RetailModel.Tenant, DatasetSchema.Empty, RetailDataGenerator.StoreCount, "test", Created),
        ];
        Store = new FakeExtractStore(Directory, new Dictionary<Guid, string>
        {
            [RetailModel.SalesVersionId] = Files.Sales,
            [RetailModel.ProductsVersionId] = Files.Products,
            [RetailModel.StoresVersionId] = Files.Stores,
        });
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp files; a locked file on Windows is not worth failing the run.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Runs SQL directly against the Parquet files (independent of the compiler) to compute expected results.</summary>
    public async Task<List<object?[]>> QueryDirectAsync(string sql)
    {
        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql
            .Replace("{sales}", $"read_parquet('{Fwd(Files.Sales)}')", StringComparison.Ordinal)
            .Replace("{products}", $"read_parquet('{Fwd(Files.Products)}')", StringComparison.Ordinal)
            .Replace("{stores}", $"read_parquet('{Fwd(Files.Stores)}')", StringComparison.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Schema of a dataset version, for implicit-model tests.</summary>
    public static DatasetSchema SalesSchema { get; } = new(
    [
        new SchemaColumn("order_id", DataType.Integer),
        new SchemaColumn("channel", DataType.String),
        new SchemaColumn("revenue", DataType.Decimal),
    ]);

    private static string Fwd(string path) => path.Replace('\\', '/');
}

/// <summary>Maps dataset version ids to existing local files; never touches Blob Storage.</summary>
public sealed class FakeExtractStore(string root, IReadOnlyDictionary<Guid, string> files) : IExtractStore
{
    public string LocalRoot => root;

    public int Requests { get; private set; }

    public Task<string> GetLocalPathAsync(DatasetVersion version, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(files[version.Id]);
    }

    public Task<Uri> SaveAsync(TenantId tenant, Guid datasetVersionId, Stream parquet, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
