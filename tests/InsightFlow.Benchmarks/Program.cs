using BenchmarkDotNet.Running;

// TODO(roy): M4 — CompilerBenchmarks and DuckDbGroupByBenchmarks over the generated 10M-row retail Parquet file.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
