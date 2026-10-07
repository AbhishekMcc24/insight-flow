using DuckDB.NET.Data;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;

namespace InsightFlow.Connectors.Tests;

/// <summary>
/// The quality bar every connector must meet. A connector's test class inherits this and supplies a reachable
/// profile with a known table; the suite then checks testing, discovery, extraction (via the real
/// <see cref="DuckDbExtractWriter"/>), row caps, unknown tables and cancellation. See docs/handoff-dev2.md.
/// </summary>
public abstract class ConnectorContractTests
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract DataSourceKind ExpectedKind { get; }

    /// <summary>Id (as returned by discovery) of a table with <see cref="ExpectedRowCount"/> rows.</summary>
    protected abstract string ExpectedTableId { get; }

    protected abstract long ExpectedRowCount { get; }

    /// <summary>A subset of the columns the expected table must have.</summary>
    protected abstract IReadOnlyCollection<string> ExpectedColumns { get; }

    protected abstract IDataSourceConnector CreateConnector();

    protected abstract Task<ConnectionProfile> CreateValidProfileAsync();

    /// <summary>A well-formed profile whose source cannot be reached (wrong host, missing file…).</summary>
    protected abstract ConnectionProfile CreateUnreachableProfile();

    /// <summary>Override to skip (e.g. when no test database is configured).</summary>
    protected virtual void SkipIfUnavailable()
    {
    }

    [Fact]
    public void Kind_MatchesDeclaredKind()
    {
        CreateConnector().Kind.ShouldBe(ExpectedKind);
    }

    [Fact]
    public async Task Test_ValidProfile_Succeeds()
    {
        SkipIfUnavailable();

        var result = await CreateConnector().TestAsync(await CreateValidProfileAsync(), Ct);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public async Task Test_UnreachableProfile_FailsWithoutThrowing()
    {
        SkipIfUnavailable();

        var result = await CreateConnector().TestAsync(CreateUnreachableProfile(), Ct);

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Discover_ValidProfile_ListsExpectedTable()
    {
        SkipIfUnavailable();
        var tables = new List<SourceTable>();

        await foreach (var table in CreateConnector().DiscoverAsync(await CreateValidProfileAsync(), Ct))
        {
            tables.Add(table);
        }

        tables.ShouldContain(t => t.Id == ExpectedTableId);
    }

    [Fact]
    public async Task Extract_ExpectedTable_WritesAllRowsAndColumns()
    {
        SkipIfUnavailable();

        var output = await ExtractAsync(new ExtractRequest(await CreateValidProfileAsync(), ExpectedTableId));

        output.RowCount.ShouldBe(ExpectedRowCount);
        ExpectedColumns.Except(output.Schema.Columns.Select(c => c.Name)).ShouldBeEmpty("missing columns");
        (await CountParquetRowsAsync(output.ParquetPath)).ShouldBe(ExpectedRowCount);
    }

    [Fact]
    public async Task Extract_WithMaxRows_StopsEarly()
    {
        SkipIfUnavailable();

        var output = await ExtractAsync(new ExtractRequest(await CreateValidProfileAsync(), ExpectedTableId, MaxRows: 7));

        output.RowCount.ShouldBe(Math.Min(7, ExpectedRowCount));
    }

    [Fact]
    public async Task Extract_UnknownTable_ThrowsConnectorException()
    {
        SkipIfUnavailable();
        var request = new ExtractRequest(await CreateValidProfileAsync(), "no_such_schema.no_such_table");

        await Should.ThrowAsync<ConnectorException>(() => ExtractAsync(request));
    }

    [Fact]
    public async Task Extract_CancelledToken_Throws()
    {
        SkipIfUnavailable();
        var request = new ExtractRequest(await CreateValidProfileAsync(), ExpectedTableId);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var directory = NewWorkDirectory();
        await using var writer = await DuckDbExtractWriter.CreateAsync(directory, "512MB", Ct);

        await Should.ThrowAsync<OperationCanceledException>(() => CreateConnector().ExtractAsync(request, writer, cancelled.Token));
    }

    protected static string NewWorkDirectory() =>
        Path.Combine(Path.GetTempPath(), "insightflow-connector-tests", Guid.NewGuid().ToString("N"));

    private async Task<ExtractOutput> ExtractAsync(ExtractRequest request)
    {
        await using var writer = await DuckDbExtractWriter.CreateAsync(NewWorkDirectory(), "512MB", Ct);
        await CreateConnector().ExtractAsync(request, writer, Ct);
        return await writer.CompleteAsync(Ct);
    }

    private static async Task<long> CountParquetRowsAsync(string path)
    {
        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM read_parquet('{path.Replace('\\', '/')}')";
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
