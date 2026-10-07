using DuckDB.NET.Data;

namespace InsightFlow.Testing;

/// <summary>
/// Deterministic "Contoso Retail" data matching <see cref="RetailModel"/>: <c>sales</c> (fact), <c>products</c>, <c>stores</c>.
/// Values come from a SplitMix64 generator (not DuckDB's <c>random()</c>/<c>hash()</c>), so the same row count yields
/// identical data on every machine and DuckDB version — tests, benchmarks and the eval set can rely on exact numbers.
/// </summary>
public static class RetailDataGenerator
{
    public const int StoreCount = 40;
    public const int ProductCount = 120;
    public const ulong Seed = 20_261_007;

    public static readonly string[] Regions = ["North", "South", "East", "West", "Central"];
    public static readonly string[] Countries = ["Germany", "France", "Spain", "Italy", "Netherlands", "United Kingdom"];
    public static readonly string[] Categories = ["Electronics", "Home", "Garden", "Toys", "Clothing", "Sports"];

    public static readonly DateOnly FirstOrderDate = new(2023, 1, 1);
    public const int OrderDateSpanDays = 1_096; // 2023-01-01 .. 2025-12-31

    /// <summary>Paths of the three normalized Parquet files.</summary>
    public sealed record NormalizedFiles(string Sales, string Products, string Stores);

    /// <summary>Writes <c>sales.parquet</c>, <c>products.parquet</c> and <c>stores.parquet</c> into <paramref name="directory"/>.</summary>
    public static async Task<NormalizedFiles> WriteNormalizedParquetAsync(string directory, int salesRows, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        await using var connection = await LoadAsync(salesRows, cancellationToken);
        var files = new NormalizedFiles(
            Path.Combine(directory, "sales.parquet"),
            Path.Combine(directory, "products.parquet"),
            Path.Combine(directory, "stores.parquet"));

        await ExecAsync(connection, $"COPY sales TO {Literal(files.Sales)} (FORMAT PARQUET)", cancellationToken);
        await ExecAsync(connection, $"COPY products TO {Literal(files.Products)} (FORMAT PARQUET)", cancellationToken);
        await ExecAsync(connection, $"COPY stores TO {Literal(files.Stores)} (FORMAT PARQUET)", cancellationToken);
        return files;
    }

    /// <summary>Writes one denormalized table (<c>retail_sales</c>: sales joined with products and stores) as CSV or Parquet.</summary>
    public static async Task WriteDenormalizedAsync(string path, int salesRows, bool csv, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var connection = await LoadAsync(salesRows, cancellationToken);
        const string select = """
            SELECT s.order_id, s.order_date, s.channel, st.store_name, st.region, st.country,
                   p.product_name, p.category, p.subcategory, s.quantity, s.revenue, s.cost, s.is_returned
            FROM sales s JOIN products p USING (product_id) JOIN stores st USING (store_id)
            ORDER BY s.order_id
            """;
        var format = csv ? "FORMAT CSV, HEADER" : "FORMAT PARQUET";
        await ExecAsync(connection, $"COPY ({select}) TO {Literal(path)} ({format})", cancellationToken);
    }

    /// <summary>The list price of a product (deterministic from its id).</summary>
    public static decimal UnitPrice(int productId) => 5m + ((productId * 37) % 496) + 0.99m;

    private static async Task<DuckDBConnection> LoadAsync(int salesRows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(salesRows, 1);
        var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);

        await ExecAsync(connection, """
            CREATE TABLE stores (store_id INTEGER, store_name VARCHAR, region VARCHAR, country VARCHAR);
            CREATE TABLE products (product_id INTEGER, product_name VARCHAR, category VARCHAR, subcategory VARCHAR);
            CREATE TABLE sales (
                order_id BIGINT, order_date DATE, ordered_at TIMESTAMP, store_id INTEGER, product_id INTEGER,
                channel VARCHAR, quantity INTEGER, revenue DECIMAL(18,2), cost DECIMAL(18,2), is_returned BOOLEAN);
            """, ct);

        using (var appender = connection.CreateAppender("stores"))
        {
            for (var id = 1; id <= StoreCount; id++)
            {
                appender.CreateRow()
                    .AppendValue((int?)id)
                    .AppendValue($"Store {id:000}")
                    .AppendValue(Regions[(id - 1) % Regions.Length])
                    .AppendValue(Countries[(id * 7) % Countries.Length])
                    .EndRow();
            }
        }

        using (var appender = connection.CreateAppender("products"))
        {
            for (var id = 1; id <= ProductCount; id++)
            {
                var category = Categories[(id - 1) % Categories.Length];
                appender.CreateRow()
                    .AppendValue((int?)id)
                    .AppendValue($"Product {id:000}")
                    .AppendValue(category)
                    .AppendValue($"{category} {(id % 4) + 1}")
                    .EndRow();
            }
        }

        var rng = new SplitMix64(Seed);
        using (var appender = connection.CreateAppender("sales"))
        {
            for (var i = 0; i < salesRows; i++)
            {
                var orderDate = FirstOrderDate.AddDays(rng.NextInt(OrderDateSpanDays));
                var orderedAt = orderDate.ToDateTime(TimeOnly.MinValue).AddSeconds(rng.NextInt(86_400));
                var storeId = rng.NextInt(StoreCount) + 1;
                var productId = rng.NextInt(ProductCount) + 1;
                var channel = rng.NextDouble() < 0.4 ? "Online" : "Store";
                var quantity = rng.NextInt(10) + 1;
                var revenue = quantity * UnitPrice(productId);
                var cost = Math.Round(revenue * (0.5m + ((decimal)rng.NextDouble() * 0.3m)), 2);
                var returned = rng.NextDouble() < 0.03;

                appender.CreateRow()
                    .AppendValue((long?)(i + 1))
                    .AppendValue((DateOnly?)orderDate)
                    .AppendValue((DateTime?)orderedAt)
                    .AppendValue((int?)storeId)
                    .AppendValue((int?)productId)
                    .AppendValue(channel)
                    .AppendValue((int?)quantity)
                    .AppendValue((decimal?)revenue)
                    .AppendValue((decimal?)cost)
                    .AppendValue((bool?)returned)
                    .EndRow();
            }
        }

        return connection;
    }

    private static async Task ExecAsync(DuckDBConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Literal(string path) => "'" + path.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>SplitMix64: tiny, fast, well-distributed and fully deterministic.</summary>
    private struct SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public int NextInt(int maxExclusive) => (int)(Next() % (ulong)maxExclusive);

        public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
    }
}
