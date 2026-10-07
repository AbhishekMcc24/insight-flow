# Query engine benchmarks

Measured with `tests/InsightFlow.Benchmarks` (BenchmarkDotNet, in-process, `ShortRun`). Reproduce with:

```bash
dotnet run -c Release --project tests/InsightFlow.Benchmarks -- --filter "*"
```

## Environment (baseline, 2026-10-07)

| | |
|---|---|
| Machine | Developer laptop — 12th Gen Intel Core i3-1215U (6 cores / 8 threads), Windows 11 25H2 |
| Runtime | .NET 10.0.12, RyuJIT x86-64-v3 |
| DuckDB | 1.5.6 (DuckDB.NET.Data.Full 1.5.6), default threads, `memory_limit = 4GB` |
| Dataset | `RetailDataGenerator`: 10,000,000 `sales` rows (207 MB Parquet) + 120 `products` + 40 `stores`, local SSD, warm OS cache |

This is a deliberately modest machine; Azure Container Apps replicas with more vCPUs will be faster for the
execution benchmarks (DuckDB parallelises scans across cores).

## Results

### Compile (VizSpec → DuckDB SQL, no I/O)

| Spec | Mean | Allocated |
|---|---:|---:|
| Single table, month time unit + color | 10.3 µs | 21 KB |
| One join (revenue by region) | 6.9 µs | 17 KB |
| Top-N with two joins + filter | 12.1 µs | 35 KB |

Compilation is negligible next to execution (≈ 0.01% of a 10M-row query).

### Compile + execute on 10M rows (no result cache)

Each run opens a fresh in-memory DuckDB connection, mounts the Parquet files as views, locks the configuration
down and executes — i.e. exactly the production path of `DuckDbQueryExecutor` minus the Redis cache.

| Query | Mean | StdDev | Rows out |
|---|---:|---:|---:|
| `SUM(revenue)` by store region (join to `stores`) | **166 ms** | 2.3 ms | 5 |
| `SUM(revenue)` by month × channel (`date_trunc` + cast) | **197 ms** | 2.7 ms | 72 |
| Top-10 products in Germany (filter + Top-N subquery, two joins) | **171 ms** | 2.8 ms | 10 |

A repeat of the same chart is served from Redis (`insightflow.query.cache.hits`) without touching DuckDB.

## Notes and follow-ups

- **Cold start:** the first query on a QueryService replica also downloads the extract from Blob Storage into the
  local LRU cache (bounded by `QueryEngine:MaxCacheBytes`); that cost is network-bound and not included above.
- **TODO(roy):** add a cold-cache benchmark against Azurite, and re-baseline on the ACA SKU chosen in M9.
- Generating the 10M-row dataset takes well under a minute and happens once per machine.
