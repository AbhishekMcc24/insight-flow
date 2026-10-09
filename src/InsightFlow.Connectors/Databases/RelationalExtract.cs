using System.Data.Common;
using System.Globalization;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Connectors.Databases;

/// <summary>Shared pieces of the relational connectors: identifier quoting, CLR type mapping and row streaming.</summary>
internal static class RelationalExtract
{
    public static DataType MapClrType(Type? type) => type switch
    {
        _ when type == typeof(long) || type == typeof(int) || type == typeof(short) || type == typeof(byte) => DataType.Integer,
        _ when type == typeof(decimal) || type == typeof(double) || type == typeof(float) => DataType.Decimal,
        _ when type == typeof(bool) => DataType.Boolean,
        _ when type == typeof(DateOnly) => DataType.Date,
        _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => DataType.DateTime,
        _ => DataType.String,
    };

    public static string Quote(string identifier, char mark)
    {
        var escaped = identifier.Replace(mark.ToString(), new string(mark, 2), StringComparison.Ordinal);
        return $"{mark}{escaped}{mark}";
    }

    /// <summary>First line of a driver message, with the secret removed so it can be shown to the user.</summary>
    public static string SafeMessage(string message, string? secret)
    {
        var line = message.Split('\n', '\r')[0].Trim();
        if (string.IsNullOrEmpty(secret) || secret.Length < 3)
        {
            return line;
        }

        line = line.Replace(secret, "****", StringComparison.Ordinal);
        var escaped = Uri.EscapeDataString(secret);
        return string.Equals(escaped, secret, StringComparison.Ordinal)
            ? line
            : line.Replace(escaped, "****", StringComparison.Ordinal);
    }

    public static async Task<ExtractResult> CopyRowsAsync(
        DbDataReader reader, IReadOnlyList<SchemaColumn> columns, IExtractWriter writer, CancellationToken cancellationToken)
    {
        await writer.BeginTableAsync(columns, cancellationToken);
        var values = new object?[columns.Count];
        long rows = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
            }

            try
            {
                writer.AppendRow(values);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
            {
                throw new ConnectorException("A value could not be converted to the declared column type.", ex);
            }

            rows++;
        }

        return ExtractResult.Rows(rows);
    }

    public static int ParsePort(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : throw new ConnectorException("Setting 'port' must be a number from 1 to 65535.");
    }
}
