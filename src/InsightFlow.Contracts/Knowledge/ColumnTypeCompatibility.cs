namespace InsightFlow.Contracts.Knowledge;

/// <summary>
/// Decides whether two columns can be related by their data types. Two columns whose types are different kinds of value (text and a
/// number, a date and a flag) cannot be joined meaningfully, so a relationship between them is refused. Types in the same family are
/// accepted even when they are not spelled the same (<c>int</c> and <c>bigint</c>, <c>varchar(50)</c> and <c>nvarchar(200)</c>), because
/// databases join those without conversion. Lives in Contracts so the browser and the API apply the very same rule.
/// </summary>
public static class ColumnTypeCompatibility
{
    private static readonly Dictionary<string, string> Families = BuildFamilies();

    /// <summary>True when a relationship between columns of these two types is possible.</summary>
    public static bool AreCompatible(string? first, string? second)
    {
        var a = Normalize(first);
        var b = Normalize(second);
        if (a.Length == 0 || b.Length == 0 || a == "unknown" || b == "unknown")
        {
            return true; // nothing to compare (a hand-made column with no type yet)
        }

        var familyA = Families.GetValueOrDefault(a, a);
        var familyB = Families.GetValueOrDefault(b, b);
        return string.Equals(familyA, familyB, StringComparison.Ordinal);
    }

    /// <summary>The family a type belongs to (number, text, datetime, ...), or the type itself when it is not a known one. For messages and tests.</summary>
    public static string FamilyOf(string? type)
    {
        var t = Normalize(type);
        return Families.GetValueOrDefault(t, t);
    }

    /// <summary>Lower-case, without a length/precision such as <c>(50)</c> or <c>(10,2)</c>, an <c>unsigned</c> suffix, or an array/identity marker.</summary>
    internal static string Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return string.Empty;
        }

        var text = type.Trim().ToLowerInvariant();
        var paren = text.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            var close = text.IndexOf(')', paren);
            text = close > paren ? (text[..paren] + text[(close + 1)..]).Trim() : text[..paren].Trim();
        }

        foreach (var suffix in new[] { " unsigned", " identity", "[]" })
        {
            text = text.Replace(suffix, string.Empty, StringComparison.Ordinal);
        }

        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static Dictionary<string, string> BuildFamilies()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string family, params string[] types)
        {
            foreach (var t in types)
            {
                map[t] = family;
            }
        }

        Add("number", "int", "integer", "bigint", "smallint", "tinyint", "mediumint", "int2", "int4", "int8", "serial", "bigserial", "smallserial",
            "decimal", "numeric", "number", "money", "smallmoney", "float", "float4", "float8", "double", "double precision", "real");
        Add("text", "char", "varchar", "nchar", "nvarchar", "text", "ntext", "tinytext", "mediumtext", "longtext", "string", "citext",
            "character", "character varying", "bpchar", "name", "varchar2", "nvarchar2", "clob", "xml", "json", "jsonb");
        Add("datetime", "date", "datetime", "datetime2", "smalldatetime", "timestamp", "timestamptz", "datetimeoffset", "time", "timetz", "year",
            "timestamp without time zone", "timestamp with time zone", "time without time zone", "time with time zone");
        Add("boolean", "bit", "bool", "boolean");
        Add("uuid", "uniqueidentifier", "uuid");
        Add("binary", "binary", "varbinary", "blob", "bytea", "image", "longblob", "mediumblob", "tinyblob");
        return map;
    }
}
