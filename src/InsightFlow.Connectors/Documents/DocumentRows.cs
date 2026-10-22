using System.Globalization;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Connectors.Documents;

/// <summary>
/// Shared sampling rules for document stores (D13): nested objects become dotted columns, arrays become JSON text,
/// and conflicting types widen to <see cref="DataType.String"/>. Only the sampled rows decide the schema; later
/// documents project onto those columns.
/// </summary>
internal static class DocumentRows
{
    internal readonly record struct Cell(DataType Type, object? Value);

    public static DataType Widen(DataType current, DataType observed)
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

    public static async Task<ExtractResult> WriteAsync(
        IExtractWriter writer,
        int sampleSize,
        IAsyncEnumerable<IReadOnlyDictionary<string, Cell>> rows,
        CancellationToken cancellationToken)
    {
        var order = new List<string>();
        var types = new Dictionary<string, DataType>(StringComparer.Ordinal);
        var buffer = new List<IReadOnlyDictionary<string, Cell>>();
        var started = false;
        long count = 0;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!started)
            {
                Observe(row, order, types);
                buffer.Add(row);
                if (buffer.Count < sampleSize)
                {
                    continue;
                }

                await FlushAsync();
                continue;
            }

            writer.AppendRow(Project(order, types, row));
            count++;
        }

        if (!started)
        {
            if (order.Count == 0)
            {
                throw new ConnectorException("The source has no documents to infer a schema from.");
            }

            await FlushAsync();
        }

        return ExtractResult.Rows(count);

        async Task FlushAsync()
        {
            var columns = order.Select(name => new SchemaColumn(name, types[name])).ToList();
            await writer.BeginTableAsync(columns, cancellationToken);
            foreach (var buffered in buffer)
            {
                writer.AppendRow(Project(order, types, buffered));
                count++;
            }

            buffer.Clear();
            started = true;
        }
    }

    public static string JsonString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public static string Format(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);

    private static void Observe(IReadOnlyDictionary<string, Cell> row, List<string> order, Dictionary<string, DataType> types)
    {
        foreach (var (key, cell) in row)
        {
            if (cell.Value is null)
            {
                continue;
            }

            if (!types.TryGetValue(key, out var existing))
            {
                order.Add(key);
                types[key] = cell.Type;
            }
            else
            {
                types[key] = Widen(existing, cell.Type);
            }
        }
    }

    private static object?[] Project(List<string> order, Dictionary<string, DataType> types, IReadOnlyDictionary<string, Cell> row)
    {
        var values = new object?[order.Count];
        for (var i = 0; i < order.Count; i++)
        {
            if (!row.TryGetValue(order[i], out var cell) || cell.Value is null)
            {
                continue;
            }

            values[i] = Coerce(types[order[i]], cell);
        }

        return values;
    }

    private static object? Coerce(DataType declared, Cell cell)
    {
        if (declared == cell.Type || cell.Value is null)
        {
            return cell.Value;
        }

        if (declared == DataType.String)
        {
            return Convert.ToString(cell.Value, CultureInfo.InvariantCulture);
        }

        if (declared == DataType.Decimal && cell.Type == DataType.Integer)
        {
            return Convert.ToDecimal(cell.Value, CultureInfo.InvariantCulture);
        }

        if (declared == DataType.DateTime && cell.Value is DateOnly date)
        {
            return date.ToDateTime(TimeOnly.MinValue);
        }

        if (declared == DataType.Date && cell.Value is DateTime dateTime)
        {
            return DateOnly.FromDateTime(dateTime);
        }

        return cell.Value;
    }
}
