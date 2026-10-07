using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using DuckDB.NET.Data;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Threads;
using InsightFlow.Query.Compilation;
using InsightFlow.Query.Dialects;
using InsightFlow.Query.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InsightFlow.Query.Execution;

/// <summary>Runs a compiled query and returns rows.</summary>
public interface IQueryExecutor
{
    /// <param name="query">SQL compiled for the executor's dialect.</param>
    /// <param name="sources">Dataset versions for every <see cref="CompiledQuery.Sources"/> entry (already tenant-checked by the caller).</param>
    /// <param name="cancellationToken">Interrupts the running query.</param>
    Task<QueryResult> ExecuteAsync(CompiledQuery query, IReadOnlyList<DatasetVersion> sources, CancellationToken cancellationToken);
}

/// <summary>
/// Executes DuckDB SQL over local Parquet extracts. Each query gets a fresh in-memory connection that can read only
/// the extract cache directory: sources are mounted as views, then external access is disabled and the configuration
/// locked before the compiled SQL runs. Cancellation (caller token or timeout) interrupts the running query.
/// </summary>
public sealed partial class DuckDbQueryExecutor(
    IExtractStore extracts,
    IOptions<QueryEngineOptions> options,
    ILogger<DuckDbQueryExecutor> logger) : IQueryExecutor
{
    public async Task<QueryResult> ExecuteAsync(CompiledQuery query, IReadOnlyList<DatasetVersion> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sources);
        if (query.Dialect != DuckDbDialect.Instance.Name)
        {
            throw new ArgumentException($"DuckDbQueryExecutor cannot run {query.Dialect} SQL.", nameof(query));
        }

        using var activity = QueryTelemetry.ActivitySource.StartActivity("query.execute");
        var started = Stopwatch.GetTimestamp();
        var settings = options.Value;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.QueryTimeout);
        var ct = timeout.Token;

        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);

        await ExecuteAsync(connection, $"SET memory_limit = '{settings.MemoryLimit}'", ct);
        if (settings.Threads > 0)
        {
            await ExecuteAsync(connection, string.Create(CultureInfo.InvariantCulture, $"SET threads = {settings.Threads}"), ct);
        }

        foreach (var source in query.Sources)
        {
            var version = sources.FirstOrDefault(v => v.Id == source.DatasetVersionId)
                ?? throw new InvalidOperationException($"No dataset version supplied for source {source.RelationName}.");
            var path = await extracts.GetLocalPathAsync(version, ct);
            await ExecuteAsync(connection, $"CREATE VIEW {DuckDbDialect.Instance.QuoteIdentifier(source.RelationName)} AS SELECT * FROM read_parquet({PathLiteral(path)})", ct);
        }

        await LockDownAsync(connection, extracts.LocalRoot, ct);

        try
        {
            var result = await ReadAsync(connection, query, ct);
            activity?.SetTag("insightflow.rows", result.Rows.Count);
            LogExecuted(logger, query.Sources.Count, result.Rows.Count, result.Truncated, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The query exceeded the {settings.QueryTimeout.TotalSeconds:N0}s time limit.");
        }
    }

    /// <summary>Only the extract cache stays readable; nothing (including the compiled SQL) can change that afterwards.</summary>
    internal static async Task LockDownAsync(DuckDBConnection connection, string readableRoot, CancellationToken ct)
    {
        await ExecuteAsync(connection, $"SET allowed_directories = [{PathLiteral(readableRoot)}]", ct);
        await ExecuteAsync(connection, "SET enable_external_access = false", ct);
        await ExecuteAsync(connection, "SET lock_configuration = true", ct);
    }

    private static async Task<QueryResult> ReadAsync(DuckDBConnection connection, CompiledQuery query, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = query.Sql;
        foreach (var parameter in query.Parameters)
        {
            command.Parameters.Add(new DuckDBParameter(parameter.Name, parameter.Value ?? DBNull.Value));
        }

        await using var reader = await command.ExecuteReaderAsync(ct);

        var columns = new List<QueryColumn>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            var meta = query.Columns.FirstOrDefault(c => c.Name == name);
            columns.Add(new QueryColumn(name, MapType(reader.GetFieldType(i)), meta?.Channel, meta?.Field));
        }

        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == query.RowLimit)
            {
                truncated = true;
                break;
            }

            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }

            rows.Add(row);
        }

        return new QueryResult(columns, rows, truncated);
    }

    internal static ColumnType MapType(Type type) => type switch
    {
        _ when type == typeof(string) => ColumnType.String,
        _ when type == typeof(bool) => ColumnType.Boolean,
        _ when type == typeof(DateOnly) => ColumnType.Date,
        _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => ColumnType.DateTime,
        _ when type == typeof(long) || type == typeof(int) || type == typeof(short) || type == typeof(sbyte)
               || type == typeof(ulong) || type == typeof(uint) || type == typeof(ushort) || type == typeof(byte)
               || type == typeof(BigInteger) => ColumnType.Integer,
        _ when type == typeof(decimal) || type == typeof(double) || type == typeof(float) => ColumnType.Number,
        _ => ColumnType.Other,
    };

    /// <summary>Converts DuckDB values to JSON-friendly primitives.</summary>
    internal static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        string or bool or long or decimal or DateOnly => value,
        int i => (long)i,
        short s => (long)s,
        sbyte sb => (long)sb,
        byte b => (long)b,
        ushort us => (long)us,
        uint ui => (long)ui,
        ulong ul => ul <= long.MaxValue ? (long)ul : (decimal)ul,
        BigInteger big => big >= long.MinValue && big <= long.MaxValue ? (long)big : big.ToString(CultureInfo.InvariantCulture),
        double d => double.IsFinite(d) ? d : null,
        float f => float.IsFinite(f) ? (double)f : null,
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>DuckDB accepts forward slashes on every OS; ids-only paths never contain quotes, but escape anyway.</summary>
    private static string PathLiteral(string path) => Literal(path.Replace('\\', '/'));

    private static async Task ExecuteAsync(DuckDBConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "DuckDB query over {SourceCount} source(s) returned {RowCount} rows (truncated: {Truncated}) in {ElapsedMs:F1} ms")]
    private static partial void LogExecuted(ILogger logger, int sourceCount, int rowCount, bool truncated, double elapsedMs);
}
