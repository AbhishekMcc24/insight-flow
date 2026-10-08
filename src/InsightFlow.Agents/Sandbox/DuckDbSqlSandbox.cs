using System.Diagnostics;
using System.Globalization;
using DuckDB.NET.Data;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Threads;
using InsightFlow.Query.Execution;
using InsightFlow.Query.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InsightFlow.Agents.Sandbox;

/// <summary>
/// In-process <see cref="ISqlSandbox"/> on a fresh in-memory DuckDB per request:
/// <list type="number">
/// <item>the SQL passes <see cref="SqlGuard"/> (DuckDB parser + AST allow-list);</item>
/// <item>inputs are copied into tables <c>input</c>, <c>input_2</c>… from the tenant's Parquet extracts;</item>
/// <item>only a fresh per-request output directory stays readable, external access is disabled and the configuration is locked;</item>
/// <item>the SQL runs wrapped as <c>CREATE TEMP TABLE __result AS SELECT * FROM (…) LIMIT cap+1</c> under a timeout that interrupts DuckDB;</item>
/// <item>trusted code reads the preview/schema and, if requested, writes the full result to Parquet in the output directory.</item>
/// </list>
/// Interruption was verified to work reliably in-process (tests), so no child-process isolation is used; see ADR 0022.
/// </summary>
public sealed partial class DuckDbSqlSandbox(
    IExtractStore extracts,
    IOptions<SandboxOptions> options,
    ILogger<DuckDbSqlSandbox> logger) : ISqlSandbox
{
    public static string InputTableName(int index) => index == 0 ? "input" : string.Create(CultureInfo.InvariantCulture, $"input_{index + 1}");

    public async Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = AgentsTelemetry.ActivitySource.StartActivity("sandbox.run");
        var started = Stopwatch.GetTimestamp();
        var settings = options.Value;
        var inputNames = request.Inputs.Select((_, i) => InputTableName(i)).ToList();

        if (request.Inputs.Count == 0 || request.Inputs.Any(v => v.TenantId != request.Tenant))
        {
            throw new InvalidOperationException("Sandbox inputs must exist and belong to the calling tenant.");
        }

        var guard = await SqlGuard.CheckAsync(request.Sql, inputNames, cancellationToken);
        if (!guard.IsAllowed)
        {
            AgentsTelemetry.SandboxRuns.Add(1, new KeyValuePair<string, object?>("status", "rejected"));
            LogRejected(logger, guard.Rejection.ToString());
            return new SandboxResult(SandboxStatus.Rejected, null, 0, null, guard.Message, null, null, Elapsed(started), guard.Rejection);
        }

        var cap = request.Materialize ? settings.MaxMaterializedRows : Math.Min(request.RowCap ?? settings.DefaultRowCap, settings.DefaultRowCap);
        var workDirectory = Path.Combine(settings.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout ?? settings.DefaultTimeout);
        var ct = timeout.Token;

        try
        {
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync(ct);
            await ExecAsync(connection, $"SET memory_limit = '{settings.MemoryLimit}'", ct);
            await ExecAsync(connection, "SET threads = 4", ct);

            for (var i = 0; i < request.Inputs.Count; i++)
            {
                var path = await extracts.GetLocalPathAsync(request.Inputs[i], ct);
                await ExecAsync(connection, $"CREATE TABLE {inputNames[i]} AS SELECT * FROM read_parquet({DuckDbValues.PathLiteral(path)})", ct);
            }

            await ExecAsync(connection, $"SET allowed_directories = [{DuckDbValues.PathLiteral(workDirectory)}]", ct);
            await ExecAsync(connection, "SET enable_external_access = false", ct);
            await ExecAsync(connection, "SET lock_configuration = true", ct);

            // The newlines neutralise a trailing line comment in the SQL; the guard already proved it is a single SELECT.
            var limit = (long)cap + 1;
            await ExecAsync(connection, $"CREATE TEMP TABLE __result AS SELECT * FROM (\n{request.Sql}\n) LIMIT {limit.ToString(CultureInfo.InvariantCulture)}", ct);

            var rowCount = Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM __result", ct), CultureInfo.InvariantCulture);
            var truncated = rowCount > cap;
            var schema = await DescribeAsync(connection, ct);
            var preview = await PreviewAsync(connection, Math.Max(0, request.PreviewRows), truncated || rowCount > request.PreviewRows, ct);

            if (truncated && request.Materialize)
            {
                return Finish(new SandboxResult(SandboxStatus.TooManyRows, preview, cap, schema,
                    $"The result has more than {cap:N0} rows; narrow it down.", null, workDirectory, Elapsed(started)));
            }

            string? parquet = null;
            if (request.Materialize)
            {
                parquet = Path.Combine(workDirectory, "result.parquet");
                await ExecAsync(connection, $"COPY __result TO {DuckDbValues.PathLiteral(parquet)} (FORMAT PARQUET, COMPRESSION ZSTD)", ct);
            }

            return Finish(new SandboxResult(truncated ? SandboxStatus.TooManyRows : SandboxStatus.Succeeded, preview, Math.Min(rowCount, cap), schema,
                truncated ? $"The result was cut at {cap:N0} rows." : null, parquet, workDirectory, Elapsed(started)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Finish(new SandboxResult(SandboxStatus.TimedOut, null, 0, null,
                $"The query took longer than {(request.Timeout ?? settings.DefaultTimeout).TotalSeconds:N0} seconds and was stopped.", null, workDirectory, Elapsed(started)));
        }
        catch (DuckDBException ex)
        {
            return Finish(new SandboxResult(SandboxStatus.Failed, null, 0, null, FirstLine(ex.Message), null, workDirectory, Elapsed(started)));
        }
    }

    /// <summary>Deletes a result's scratch directory (callers do this after uploading a materialized result).</summary>
    public static void Cleanup(SandboxResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.WorkDirectory is { } dir && Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Scratch space; removed on the next cleanup.
            }
        }
    }

    private SandboxResult Finish(SandboxResult result)
    {
        AgentsTelemetry.SandboxRuns.Add(1, new KeyValuePair<string, object?>("status", result.Status.ToString().ToLowerInvariant()));
        LogCompleted(logger, result.Status.ToString(), result.RowCount, result.Duration.TotalMilliseconds);

        // Keep the scratch directory only when it holds a materialized result the caller still has to upload.
        if (result.ParquetPath is not null)
        {
            return result;
        }

        Cleanup(result);
        return result with { WorkDirectory = null };
    }

    private static async Task<DatasetSchema> DescribeAsync(DuckDBConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name = '__result' ORDER BY ordinal_position";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = new List<SchemaColumn>();
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new SchemaColumn(reader.GetString(0), DuckDbValues.ToDataType(reader.GetString(1)), reader.GetString(2) == "YES"));
        }

        return new DatasetSchema(columns);
    }

    private static async Task<QueryResult> PreviewAsync(DuckDBConnection connection, int rows, bool truncated, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM __result LIMIT {rows.ToString(CultureInfo.InvariantCulture)}";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => new QueryColumn(reader.GetName(i), DuckDbValues.ToColumnType(reader.GetFieldType(i))))
            .ToList();
        var data = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = DuckDbValues.Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }

            data.Add(row);
        }

        return new QueryResult(columns, data, truncated);
    }

    private static async Task ExecAsync(DuckDBConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(DuckDBConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
    }

    private static TimeSpan Elapsed(long started) => Stopwatch.GetElapsedTime(started);

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sandbox rejected SQL ({Rejection})")]
    private static partial void LogRejected(ILogger logger, string rejection);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sandbox run {Status}: {RowCount} rows in {ElapsedMs:F1} ms")]
    private static partial void LogCompleted(ILogger logger, string status, long rowCount, double elapsedMs);
}
