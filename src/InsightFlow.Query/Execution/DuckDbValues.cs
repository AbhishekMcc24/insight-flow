using System.Globalization;
using System.Numerics;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Modeling;

namespace InsightFlow.Query.Execution;

/// <summary>
/// Shared DuckDB ⇄ Insight Flow type and value mapping, used by the query executor and the AI-SQL sandbox so both
/// return identical JSON-friendly results.
/// </summary>
public static class DuckDbValues
{
    /// <summary>Client-facing column type of a .NET type returned by DuckDB.NET.</summary>
    public static ColumnType ToColumnType(Type type) => type switch
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

    /// <summary>Logical <see cref="DataType"/> of a DuckDB SQL type name (as in <c>information_schema.columns.data_type</c>).</summary>
    public static DataType ToDataType(string duckType)
    {
        ArgumentNullException.ThrowIfNull(duckType);
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

    /// <summary>Converts a DuckDB value to a JSON primitive (numbers, strings, booleans, dates; non-finite doubles → null).</summary>
    public static object? Normalize(object? value) => value switch
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

    /// <summary>A SQL string literal for a local file path (forward slashes work on every OS).</summary>
    public static string PathLiteral(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return "'" + path.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
