using System.Text.Json;
using DuckDB.NET.Data;

namespace InsightFlow.Agents.Sandbox;

/// <summary>Why a SQL text was rejected (stable codes for tests, telemetry and the agent's repair loop).</summary>
public enum SqlRejection
{
    None,
    Empty,
    TooLong,
    ParseError,
    NotSingleSelect,
    ForbiddenTable,
    ForbiddenFunction,
}

/// <summary>Outcome of <see cref="SqlGuard.Check"/>.</summary>
public sealed record SqlGuardResult(SqlRejection Rejection, string Message)
{
    public bool IsAllowed => Rejection == SqlRejection.None;

    public static SqlGuardResult Allowed { get; } = new(SqlRejection.None, "OK");
}

/// <summary>
/// Decides whether AI-written SQL may run, using DuckDB's own parser (<c>json_serialize_sql</c>) instead of regexes:
/// <list type="bullet">
/// <item>exactly one statement, and it must be a SELECT (DuckDB serializes nothing else — ATTACH, COPY, INSTALL, LOAD,
/// PRAGMA, SET, DDL and DML are all rejected by construction);</item>
/// <item>every base table is an input table (<c>input</c>, <c>input_2</c>…) or a CTE defined in the query;</item>
/// <item>table functions are limited to pure generators (<c>range</c>, <c>generate_series</c>, <c>unnest</c>) — this
/// blocks file readers and, importantly, <c>query()</c>, which would run arbitrary SQL even on a locked connection;</item>
/// <item>introspection and I/O-style scalar functions (<c>duckdb_*</c>, <c>pragma_*</c>, <c>read_*</c>, <c>current_setting</c>…) are denied.</item>
/// </list>
/// The guard is one of three layers: the sandbox connection also has external access disabled and its configuration locked.
/// </summary>
public static class SqlGuard
{
    public const int MaxSqlLength = 20_000;

    private static readonly HashSet<string> AllowedTableFunctions = new(StringComparer.OrdinalIgnoreCase) { "range", "generate_series", "unnest" };

    private static readonly string[] DeniedFunctionPrefixes = ["duckdb_", "pragma_", "read_", "sniff_", "parquet_", "iceberg_", "delta_", "sqlite_", "postgres_", "mysql_"];

    private static readonly HashSet<string> DeniedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "query", "query_table", "glob", "current_setting", "getenv", "load_aws_credentials", "which_secret", "checkpoint", "force_checkpoint",
        "copy_database", "export_database", "enable_logging", "disable_logging", "duckdb_logs",
    };

    /// <summary>Checks <paramref name="sql"/>. <paramref name="inputTables"/> are the table names the sandbox will provide.</summary>
    public static async Task<SqlGuardResult> CheckAsync(string? sql, IReadOnlyCollection<string> inputTables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputTables);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new(SqlRejection.Empty, "The SQL is empty.");
        }

        if (sql.Length > MaxSqlLength)
        {
            return new(SqlRejection.TooLong, $"The SQL is longer than {MaxSqlLength:N0} characters.");
        }

        string json;
        await using (var connection = new DuckDBConnection("DataSource=:memory:"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT json_serialize_sql($sql::VARCHAR)";
            command.Parameters.Add(new DuckDBParameter("sql", sql));
            json = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        return Check(json, inputTables);
    }

    /// <summary>Checks an already-serialized parse tree (exposed for unit tests).</summary>
    internal static SqlGuardResult Check(string serializedTree, IReadOnlyCollection<string> inputTables)
    {
        using var document = JsonDocument.Parse(serializedTree);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error) && error.GetBoolean())
        {
            var message = root.TryGetProperty("error_message", out var m) ? m.GetString() : null;
            return message is not null && message.Contains("Only SELECT", StringComparison.OrdinalIgnoreCase)
                ? new(SqlRejection.NotSingleSelect, "Only a single SELECT statement is allowed.")
                : new(SqlRejection.ParseError, $"The SQL could not be parsed: {message}");
        }

        if (!root.TryGetProperty("statements", out var statements) || statements.GetArrayLength() != 1)
        {
            return new(SqlRejection.NotSingleSelect, "Exactly one SELECT statement is allowed.");
        }

        var allowedTables = new HashSet<string>(inputTables, StringComparer.OrdinalIgnoreCase);
        CollectCteNames(root, allowedTables);
        return Walk(root, allowedTables) ?? SqlGuardResult.Allowed;
    }

    private static void CollectCteNames(JsonElement element, HashSet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("cte_map") && property.Value.TryGetProperty("map", out var map))
                    {
                        foreach (var entry in map.EnumerateArray())
                        {
                            if (entry.TryGetProperty("key", out var key) && key.GetString() is { } name)
                            {
                                names.Add(name);
                            }
                        }
                    }

                    CollectCteNames(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectCteNames(item, names);
                }

                break;
        }
    }

    private static SqlGuardResult? Walk(JsonElement element, HashSet<string> allowedTables)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (Walk(item, allowedTables) is { } rejection)
                {
                    return rejection;
                }
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        if (type == "BASE_TABLE")
        {
            var table = element.GetProperty("table_name").GetString() ?? string.Empty;
            var schema = element.TryGetProperty("schema_name", out var s) ? s.GetString() : string.Empty;
            var catalog = element.TryGetProperty("catalog_name", out var c) ? c.GetString() : string.Empty;
            if (!string.IsNullOrEmpty(schema) || !string.IsNullOrEmpty(catalog) || !allowedTables.Contains(table))
            {
                return new(SqlRejection.ForbiddenTable, $"Only the tables {string.Join(", ", allowedTables)} can be queried (found '{Qualified(catalog, schema, table)}').");
            }
        }

        if (type == "TABLE_FUNCTION" && element.TryGetProperty("function", out var function)
            && function.TryGetProperty("function_name", out var tableFunction)
            && !AllowedTableFunctions.Contains(tableFunction.GetString() ?? string.Empty))
        {
            return new(SqlRejection.ForbiddenFunction, $"Table function '{tableFunction.GetString()}' is not allowed.");
        }

        if (element.TryGetProperty("function_name", out var functionName) && functionName.GetString() is { } name && IsDenied(name))
        {
            return new(SqlRejection.ForbiddenFunction, $"Function '{name}' is not allowed.");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && Walk(property.Value, allowedTables) is { } rejection)
            {
                return rejection;
            }
        }

        return null;
    }

    private static bool IsDenied(string name) =>
        DeniedFunctions.Contains(name) || DeniedFunctionPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string Qualified(string? catalog, string? schema, string table) =>
        string.Join('.', new[] { catalog, schema, table }.Where(p => !string.IsNullOrEmpty(p)));
}
