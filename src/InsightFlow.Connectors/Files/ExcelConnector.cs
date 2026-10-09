using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ExcelDataReader;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Threads;
using Microsoft.Extensions.Options;

namespace InsightFlow.Connectors.Files;

/// <summary>
/// Excel workbooks (.xlsx and .xls). Discovery returns one <see cref="SourceTable"/> per worksheet.
/// The first row is the header. Column types are inferred from the first
/// <see cref="ConnectorOptions.DocumentSampleSize"/> data rows (numbers, booleans, and date-formatted cells),
/// then every row is streamed into the writer. A null table id extracts the first worksheet, which is what
/// "Create dataset" uses.
/// </summary>
public sealed class ExcelConnector(ISourceFileAccessor files, IOptions<ConnectorOptions> options) : IDataSourceConnector
{
    static ExcelConnector() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public DataSourceKind Kind => DataSourceKind.Excel;

    public ConnectorCapabilities Capabilities => ConnectorCapabilities.FileOnly;

    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!TryGetFileId(profile, out var fileId))
        {
            return ConnectionTestResult.Failed("No uploaded file is referenced.");
        }

        return await files.ExistsAsync(profile.TenantId, fileId, cancellationToken)
            ? ConnectionTestResult.Ok("The file is available.")
            : ConnectionTestResult.Failed("The uploaded file no longer exists.");
    }

    public async IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!TryGetFileId(profile, out var fileId))
        {
            throw new ConnectorException("No uploaded file is referenced.");
        }

        await using var local = await files.DownloadAsync(profile.TenantId, fileId, ".xlsx", cancellationToken);
        using var stream = Open(local.Path);
        using var reader = CreateReader(stream);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = string.IsNullOrWhiteSpace(reader.Name) ? $"Sheet{seen.Count + 1}" : reader.Name;
            if (seen.Add(name))
            {
                yield return new SourceTable(name, name, Kind: "sheet");
            }
        }
        while (reader.NextResult());
    }

    public async Task<ExtractResult> ExtractAsync(ExtractRequest request, IExtractWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetFileId(request.Profile, out var fileId))
        {
            throw new ConnectorException("No uploaded file is referenced.");
        }

        await using var local = await files.DownloadAsync(request.Tenant, fileId, ".xlsx", cancellationToken);
        using var stream = Open(local.Path);
        using var reader = CreateReader(stream);
        if (!SelectSheet(reader, request.Table))
        {
            throw new ConnectorException($"Table '{request.Table}' was not found.");
        }

        if (!reader.Read())
        {
            throw new ConnectorException("The worksheet has no header row.");
        }

        var headers = ReadHeaders(reader);
        var sampleSize = Math.Max(1, options.Value.DocumentSampleSize);
        var sample = new List<object?[]>();
        var started = false;
        long rows = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.MaxRows is { } max && rows >= max)
            {
                break;
            }

            var values = ReadRow(reader, headers.Count);
            rows++;
            if (!started)
            {
                sample.Add(values);
                var sampleFull = sample.Count >= sampleSize;
                var hitCap = request.MaxRows is { } cap && rows >= cap;
                if (!sampleFull && !hitCap)
                {
                    continue;
                }

                await WriteSampleAsync(writer, headers, sample, cancellationToken);
                started = true;
                continue;
            }

            Append(writer, values);
        }

        if (!started)
        {
            if (sample.Count > 0)
            {
                await WriteSampleAsync(writer, headers, sample, cancellationToken);
            }
            else
            {
                await writer.BeginTableAsync(headers.Select(h => new SchemaColumn(h, DataType.String)).ToList(), cancellationToken);
            }
        }

        return ExtractResult.Rows(rows);
    }

    private static void Append(IExtractWriter writer, object?[] values)
    {
        try
        {
            writer.AppendRow(values);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new ConnectorException("A cell could not be converted to the inferred column type.", ex);
        }
    }

    private static async Task WriteSampleAsync(
        IExtractWriter writer, List<string> headers, List<object?[]> sample, CancellationToken cancellationToken)
    {
        var columns = new List<SchemaColumn>(headers.Count);
        for (var i = 0; i < headers.Count; i++)
        {
            columns.Add(new SchemaColumn(headers[i], InferType(sample, i)));
        }

        await writer.BeginTableAsync(columns, cancellationToken);
        foreach (var row in sample)
        {
            Append(writer, row);
        }
    }

    internal static DataType InferType(IReadOnlyList<object?[]> rows, int column)
    {
        DataType? type = null;
        foreach (var row in rows)
        {
            if (column >= row.Length || row[column] is null or DBNull)
            {
                continue;
            }

            var observed = Classify(row[column]!);
            type = type is null ? observed : Widen(type.Value, observed);
        }

        return type ?? DataType.String;
    }

    internal static DataType Classify(object value) => value switch
    {
        bool => DataType.Boolean,
        DateOnly => DataType.Date,
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? DataType.Date : DataType.DateTime,
        DateTimeOffset dto => dto.TimeOfDay == TimeSpan.Zero ? DataType.Date : DataType.DateTime,
        double d when IsWhole(d) => DataType.Integer,
        float f when IsWhole(f) => DataType.Integer,
        decimal m when m == decimal.Truncate(m) => DataType.Integer,
        double or float or decimal or int or long or short => value is int or long or short ? DataType.Integer : DataType.Decimal,
        _ => DataType.String,
    };

    internal static DataType Widen(DataType current, DataType observed)
    {
        if (current == observed)
        {
            return current;
        }

        if (current is DataType.Integer && observed is DataType.Decimal || current is DataType.Decimal && observed is DataType.Integer)
        {
            return DataType.Decimal;
        }

        if (current is DataType.Date && observed is DataType.DateTime || current is DataType.DateTime && observed is DataType.Date)
        {
            return DataType.DateTime;
        }

        return DataType.String;
    }

    private static bool IsWhole(double value) =>
        value is >= long.MinValue and <= long.MaxValue && value == Math.Truncate(value);

    private static List<string> ReadHeaders(IExcelDataReader reader)
    {
        var headers = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var raw = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim();
            var name = string.IsNullOrEmpty(raw) ? $"column_{i + 1}" : raw;
            var unique = name;
            var suffix = 2;
            while (!used.Add(unique))
            {
                unique = $"{name}_{suffix}";
                suffix++;
            }

            headers.Add(unique);
        }

        if (headers.Count == 0)
        {
            throw new ConnectorException("The worksheet has no columns.");
        }

        return headers;
    }

    private static object?[] ReadRow(IExcelDataReader reader, int width)
    {
        var values = new object?[width];
        var count = Math.Min(width, reader.FieldCount);
        for (var i = 0; i < count; i++)
        {
            var value = reader.GetValue(i);
            values[i] = value is DBNull ? null : value;
        }

        return values;
    }

    private static bool SelectSheet(IExcelDataReader reader, string? table)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return true;
        }

        do
        {
            if (string.Equals(reader.Name, table, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        while (reader.NextResult());
        return false;
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static IExcelDataReader CreateReader(Stream stream)
    {
        try
        {
            return ExcelReaderFactory.CreateReader(stream);
        }
        catch (Exception ex) when (ex is not ConnectorException and not OperationCanceledException)
        {
            throw new ConnectorException("The Excel file could not be read.", ex);
        }
    }

    private static bool TryGetFileId(ConnectionProfile profile, out Guid fileId) =>
        Guid.TryParse(profile.GetSetting(ConnectionProfile.StoredFileIdSetting), out fileId);
}
