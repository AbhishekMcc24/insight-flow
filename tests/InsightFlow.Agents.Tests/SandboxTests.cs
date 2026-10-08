using System.Diagnostics;
using DuckDB.NET.Data;
using InsightFlow.Agents.Sandbox;
using InsightFlow.Domain.Modeling;
using InsightFlow.Testing;

namespace InsightFlow.Agents.Tests;

public sealed class SandboxTests(SandboxFixture data) : IClassFixture<SandboxFixture>
{
    private const string Heavy = "SELECT count(*) FROM range(100000000000) a, range(10) b WHERE a.range % 7 = b.range";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SandboxRequest Request(string sql, TimeSpan? timeout = null, int? rowCap = null, bool materialize = false) =>
        new(RetailModel.Tenant, [data.Retail], sql, timeout, rowCap, Materialize: materialize);

    [Fact]
    public async Task Run_Aggregate_ReturnsPreviewSchemaAndCount()
    {
        var result = await data.CreateSandbox().RunAsync(Request("SELECT region, SUM(revenue) AS revenue FROM input GROUP BY region ORDER BY region"), Ct);

        result.Status.ShouldBe(SandboxStatus.Succeeded, result.Error);
        result.RowCount.ShouldBe(RetailDataGenerator.Regions.Length);
        result.Schema!.Columns.Select(c => (c.Name, c.DataType)).ShouldBe([("region", DataType.String), ("revenue", DataType.Decimal)]);
        result.Preview!.Rows.Select(r => r[0]).ShouldBe(RetailDataGenerator.Regions.Order().Cast<object?>());
        result.ParquetPath.ShouldBeNull();
        result.WorkDirectory.ShouldBeNull();
    }

    [Theory]
    [InlineData("SELECT * FROM read_csv('/etc/passwd')")]
    [InlineData("ATTACH 'x.db'; SELECT 1")]
    [InlineData("COPY input TO 'exfil.csv'")]
    [InlineData("SELECT * FROM query('DROP TABLE input')")]
    public async Task Run_MaliciousSql_IsRejectedWithoutExecuting(string sql)
    {
        var result = await data.CreateSandbox().RunAsync(Request(sql), Ct);

        result.Status.ShouldBe(SandboxStatus.Rejected);
        result.Rejection.ShouldNotBe(SqlRejection.None);
    }

    /// <summary>The cancellation proof required by the prompt: a query that would run for hours is interrupted by the timeout.</summary>
    [Fact]
    public async Task Run_LongQuery_IsInterruptedByTimeout()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await data.CreateSandbox().RunAsync(Request(Heavy, timeout: TimeSpan.FromSeconds(1)), Ct);

        stopwatch.Stop();
        result.Status.ShouldBe(SandboxStatus.TimedOut);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6), "DuckDB must be interrupted, not merely abandoned");
    }

    [Fact]
    public async Task Run_CallerCancellation_InterruptsRunningQuery()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() => data.CreateSandbox().RunAsync(Request(Heavy, timeout: TimeSpan.FromMinutes(5)), cts.Token));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task Run_InterruptedQuery_LeavesNoBusyThread()
    {
        // Ten timed-out runs in a row finish quickly only if each interrupted query actually released its worker.
        var sandbox = data.CreateSandbox();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            (await sandbox.RunAsync(Request(Heavy, timeout: TimeSpan.FromMilliseconds(300)), Ct)).Status.ShouldBe(SandboxStatus.TimedOut);
        }

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Run_RowCap_TruncatesAndSaysSo()
    {
        var result = await data.CreateSandbox().RunAsync(Request("SELECT * FROM input", rowCap: 10), Ct);

        result.Status.ShouldBe(SandboxStatus.TooManyRows);
        result.RowCount.ShouldBe(10);
        result.Preview!.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task Run_BadColumn_FailsWithUsefulError()
    {
        var result = await data.CreateSandbox().RunAsync(Request("SELECT no_such_column FROM input"), Ct);

        result.Status.ShouldBe(SandboxStatus.Failed);
        result.Error!.ShouldContain("no_such_column");
    }

    [Fact]
    public async Task Run_TrailingLineComment_CannotBreakTheWrapper()
    {
        var result = await data.CreateSandbox().RunAsync(Request("SELECT COUNT(*) AS n FROM input -- comment"), Ct);

        result.Status.ShouldBe(SandboxStatus.Succeeded, result.Error);
        result.Preview!.Rows[0][0].ShouldBe((long)SandboxFixture.Rows);
    }

    [Fact]
    public async Task Run_Materialize_WritesFullResultAsParquet()
    {
        var result = await data.CreateSandbox().RunAsync(Request("SELECT *, revenue - cost AS margin FROM input", materialize: true), Ct);
        try
        {
            result.Status.ShouldBe(SandboxStatus.Succeeded, result.Error);
            result.RowCount.ShouldBe(SandboxFixture.Rows);
            result.Schema!.Find("margin")!.DataType.ShouldBe(DataType.Decimal);
            File.Exists(result.ParquetPath).ShouldBeTrue();

            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM read_parquet('{result.ParquetPath!.Replace('\\', '/')}')";
            (await command.ExecuteScalarAsync(Ct)).ShouldBe((long)SandboxFixture.Rows);
        }
        finally
        {
            DuckDbSqlSandbox.Cleanup(result);
        }

        Directory.Exists(result.WorkDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Run_MaterializeOverCap_IsAnErrorNotASilentTruncation()
    {
        var sandbox = data.CreateSandbox(new SandboxOptions
        {
            MaxMaterializedRows = 100,
            WorkDirectory = Path.Combine(data.Directory, "work-cap"),
        });

        var result = await sandbox.RunAsync(Request("SELECT * FROM input", materialize: true), Ct);
        DuckDbSqlSandbox.Cleanup(result);

        result.Status.ShouldBe(SandboxStatus.TooManyRows);
        result.ParquetPath.ShouldBeNull();
    }

    [Fact]
    public async Task Run_InputOfOtherTenant_IsRefused()
    {
        var request = new SandboxRequest(RetailModel.OtherTenant, [data.Retail], "SELECT 1");

        await Should.ThrowAsync<InvalidOperationException>(() => data.CreateSandbox().RunAsync(request, Ct));
    }
}
