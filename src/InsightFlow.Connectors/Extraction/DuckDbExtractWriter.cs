using System.Globalization;
using DuckDB.NET.Data;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Connectors.Extraction;

/// <summary>Formats DuckDB can import natively (streaming, without passing rows through .NET).</summary>
public enum ExtractFileFormat
{
    Csv,
    Parquet,
}

/// <summary>The local Parquet file produced by a writer, with its physical schema and row count.</summary>
public sealed record ExtractOutput(string ParquetPath, DatasetSchema Schema, long RowCount);

/// <summary>
/// Sink for one extract. Database connectors declare columns and stream rows (<see cref="BeginTableAsync"/> +
/// <see cref="AppendRow"/>); file connectors hand over a local file (<see cref="ImportFileAsync"/>). Either way the
/// data lands in a DuckDB table and <see cref="CompleteAsync"/> writes it as Parquet.
/// </summary>
public interface IExtractWriter : IAsyncDisposable
{
    Task BeginTableAsync(IReadOnlyList<SchemaColumn> columns, CancellationToken cancellationToken);

    /// <summary>Appends one row; values are in column order and are converted to the declared <see cref="DataType"/>.</summary>
    void AppendRow(ReadOnlySpan<object?> values);

    Task ImportFileAsync(string localPath, ExtractFileFormat format, long? maxRows, CancellationToken cancellationToken);

    Task<ExtractOutput> CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IExtractWriter"/> on an in-memory DuckDB database (spilling to <c>workDirectory</c> if large). Rows are
/// appended with the DuckDB Appender, so memory use stays bounded by DuckDB's buffer manager rather than the source size.
/// This is trusted code: it runs with external access enabled because it reads the connector's own temp files.
/// </summary>
public sealed class DuckDbExtractWriter : IExtractWriter
{
    private const string TableName = "extract";

    private readonly string _workDirectory;
    private readonly DuckDBConnection _connection;
    private DuckDBAppender? _appender;
    private IReadOnlyList<SchemaColumn> _columns = [];
    private bool _hasTable;

    private DuckDbExtractWriter(string workDirectory, DuckDBConnection connection)
    {
        _workDirectory = workDirectory;
        _connection = connection;
    }

    public static async Task<DuckDbExtractWriter> CreateAsync(string workDirectory, string memoryLimit, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var writer = new DuckDbExtractWriter(workDirectory, connection);
        await writer.ExecAsync($"SET memory_limit = {Literal(memoryLimit)}", cancellationToken);
        await writer.ExecAsync($"SET temp_directory = {Literal(Path.Combine(workDirectory, "duckdb-spill"))}", cancellationToken);
        return writer;
    }

    public async Task BeginTableAsync(IReadOnlyList<SchemaColumn> columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(columns);
        EnsureNoTable();
        if (columns.Count == 0)
        {
            throw new ConnectorException("The source has no columns.");
        }

        var definitions = columns.Select(c => $"{Quote(c.Name)} {DuckDbType(c.DataType)}");
        await ExecAsync($"CREATE TABLE {TableName} ({string.Join(", ", definitions)})", cancellationToken);
        _columns = columns;
        _appender = _connection.CreateAppender(TableName);
        _hasTable = true;
    }

    public void AppendRow(ReadOnlySpan<object?> values)
    {
        if (_appender is null)
        {
            throw new InvalidOperationException("Call BeginTableAsync before appending rows.");
        }

        if (values.Length != _columns.Count)
        {
            throw new ArgumentException($"Expected {_columns.Count} values, got {values.Length}.", nameof(values));
        }

        var row = _appender.CreateRow();
        for (var i = 0; i < values.Length; i++)
        {
            row = Append(row, _columns[i].DataType, values[i]);
        }

        row.EndRow();
    }

    public async Task ImportFileAsync(string localPath, ExtractFileFormat format, long? maxRows, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        EnsureNoTable();

        var reader = format switch
        {
            ExtractFileFormat.Csv => $"read_csv({Literal(localPath)}, auto_detect = true, sample_size = 20480)",
            ExtractFileFormat.Parquet => $"read_parquet({Literal(localPath)})",
            _ => throw new NotSupportedException($"Format {format} is not supported."),
        };
        var limit = maxRows is { } n ? string.Create(CultureInfo.InvariantCulture, $" LIMIT {n}") : string.Empty;

        try
        {
            await ExecAsync($"CREATE TABLE {TableName} AS SELECT * FROM {reader}{limit}", cancellationToken);
        }
        catch (DuckDBException ex)
        {
            // DuckDB's message describes the parse problem (line/column), not the data values.
            throw new ConnectorException($"The {format} file could not be read: {FirstLine(ex.Message)}", ex);
        }

        _hasTable = true;
    }

    public async Task<ExtractOutput> CompleteAsync(CancellationToken cancellationToken)
    {
        if (!_hasTable)
        {
            throw new InvalidOperationException("Nothing was written.");
        }

        if (_appender is not null)
        {
            _appender.Close();
            _appender.Dispose();
            _appender = null;
        }

        var schema = await DescribeAsync(cancellationToken);
        var rowCount = Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM {TableName}", cancellationToken), CultureInfo.InvariantCulture);
        var path = Path.Combine(_workDirectory, $"{Guid.NewGuid():N}.parquet");
        await ExecAsync($"COPY {TableName} TO {Literal(path)} (FORMAT PARQUET, COMPRESSION ZSTD)", cancellationToken);
        return new ExtractOutput(path, schema, rowCount);
    }

    public async ValueTask DisposeAsync()
    {
        _appender?.Dispose();
        await _connection.DisposeAsync();
    }

    /// <summary>Maps DuckDB physical types to logical <see cref="DataType"/>s.</summary>
    internal static DataType MapDuckDbType(string duckType)
    {
        var t = duckType.ToUpperInvariant();
        return t switch
        {
            "BIGINT" or "INTEGER" or "SMALLINT" or "TINYINT" or "HUGEINT" or "UBIGINT" or "UINTEGER" or "USMALLINT" or "UTINYINT" or "UHUGEINT" => DataType.Integer,
            "DOUBLE" or "FLOAT" or "REAL" => DataType.Decimal,
            "BOOLEAN" => DataType.Boolean,
            "DATE" => DataType.Date,
            "JSON" => DataType.Json,
            _ when t.StartsWith("DECIMAL", StringComparison.Ordinal) => DataType.Decimal,
            _ when t.StartsWith("TIMESTAMP", StringComparison.Ordinal) => DataType.DateTime,
            _ when t.Contains('[', StringComparison.Ordinal) || t.StartsWith("STRUCT", StringComparison.Ordinal) || t.StartsWith("MAP", StringComparison.Ordinal) => DataType.Json,
            _ => DataType.String,
        };
    }

    internal static string DuckDbType(DataType type) => type switch
    {
        DataType.Integer => "BIGINT",
        DataType.Decimal => "DECIMAL(38, 10)",
        DataType.Boolean => "BOOLEAN",
        DataType.Date => "DATE",
        DataType.DateTime => "TIMESTAMP",
        DataType.Json => "JSON",
        _ => "VARCHAR",
    };

    private static IDuckDBAppenderRow Append(IDuckDBAppenderRow row, DataType type, object? value)
    {
        if (value is null or DBNull)
        {
            return type switch
            {
                DataType.Integer => row.AppendValue((long?)null),
                DataType.Decimal => row.AppendValue((decimal?)null),
                DataType.Boolean => row.AppendValue((bool?)null),
                DataType.Date => row.AppendValue((DateOnly?)null),
                DataType.DateTime => row.AppendValue((DateTime?)null),
                _ => row.AppendValue((string?)null),
            };
        }

        var inv = CultureInfo.InvariantCulture;
        return type switch
        {
            DataType.Integer => row.AppendValue((long?)Convert.ToInt64(value, inv)),
            DataType.Decimal => row.AppendValue((decimal?)Convert.ToDecimal(value, inv)),
            DataType.Boolean => row.AppendValue((bool?)Convert.ToBoolean(value, inv)),
            DataType.Date => row.AppendValue((DateOnly?)(value switch
            {
                DateOnly d => d,
                DateTime dt => DateOnly.FromDateTime(dt),
                DateTimeOffset dto => DateOnly.FromDateTime(dto.UtcDateTime),
                _ => DateOnly.Parse(Convert.ToString(value, inv)!, inv),
            })),
            DataType.DateTime => row.AppendValue((DateTime?)(value switch
            {
                DateTime dt => dt,
                DateTimeOffset dto => dto.UtcDateTime,
                DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                _ => DateTime.Parse(Convert.ToString(value, inv)!, inv),
            })),
            _ => row.AppendValue(value switch
            {
                string s => s,
                byte[] bytes => Convert.ToBase64String(bytes),
                IFormattable f => f.ToString(null, inv),
                _ => value.ToString(),
            }),
        };
    }

    private async Task<DatasetSchema> DescribeAsync(CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name = '{TableName}' ORDER BY ordinal_position";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = new List<SchemaColumn>();
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new SchemaColumn(reader.GetString(0), MapDuckDbType(reader.GetString(1)), reader.GetString(2) == "YES"));
        }

        return new DatasetSchema(columns);
    }

    private void EnsureNoTable()
    {
        if (_hasTable)
        {
            throw new InvalidOperationException("An extract writer holds exactly one table.");
        }
    }

    private async Task ExecAsync(string sql, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();
}
