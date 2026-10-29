using System.Text.Json;

namespace InsightFlow.Agent.Knowledge;

/// <summary>A column of a table as the planner sees it.</summary>
public sealed record QueryColumn(string Name, string DataType, string? Description, bool IsPrimaryKey);

/// <summary>A knowledge-base table offered to the planner, named in the SQL by <see cref="Alias"/>.</summary>
public sealed record QueryTable(Guid Id, string Alias, string Name, string? Description, IReadOnlyList<QueryColumn> Columns);

/// <summary>A relationship the SQL may join on: one column pair (pairs of a composite key share <see cref="GroupId"/>).</summary>
public sealed record QueryJoin(Guid FromTableId, string FromColumn, Guid ToTableId, string ToColumn, string Kind, double Confidence, Guid? GroupId);

/// <summary>What the SQL does, read from DuckDB's parse tree: the problems found and the allowed joins it uses.</summary>
public sealed record SqlInspection(IReadOnlyList<string> Problems, IReadOnlyList<QueryJoin> UsedJoins, IReadOnlyList<QueryTable>? UsedTables = null);

/// <summary>
/// Checks AI-written SQL against the knowledge graph using DuckDB's own parse tree, not text matching: every column that is qualified
/// by a table alias must exist on that table, and every JOIN must be on a relationship the graph has (either direction; a composite
/// key must be joined on all of its pairs). Joins with USING, NATURAL or CROSS are refused, because they cannot be tied to a
/// relationship. This is what keeps the model from inventing joins.
/// </summary>
public static class QuerySqlInspector
{
    public static SqlInspection Inspect(string serializedTree, IReadOnlyList<QueryTable> tables, IReadOnlyList<QueryJoin> allowed)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(allowed);
        using var document = JsonDocument.Parse(serializedTree);

        var byAlias = tables.ToDictionary(t => t.Alias, StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var used = new List<QueryJoin>();

        // Names in scope: the alias of each table in FROM (or the table name when it has none) -> the knowledge-base table.
        var scope = new Dictionary<string, QueryTable>(StringComparer.OrdinalIgnoreCase);
        Collect(document.RootElement, scope, byAlias);

        Walk(document.RootElement, scope, allowed, problems, used);
        return new SqlInspection([.. problems.Distinct()], [.. used.Distinct()], [.. scope.Values.Distinct()]);
    }

    private static void Collect(JsonElement element, Dictionary<string, QueryTable> scope, Dictionary<string, QueryTable> byAlias)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (Type(element) == "BASE_TABLE" && element.TryGetProperty("table_name", out var name) && byAlias.TryGetValue(name.GetString() ?? string.Empty, out var table))
                {
                    var alias = element.TryGetProperty("alias", out var a) && !string.IsNullOrEmpty(a.GetString()) ? a.GetString()! : table.Alias;
                    scope[alias] = table;
                }

                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, scope, byAlias);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, scope, byAlias);
                }

                break;
        }
    }

    private static void Walk(JsonElement element, Dictionary<string, QueryTable> scope, IReadOnlyList<QueryJoin> allowed, List<string> problems, List<QueryJoin> used)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var type = Type(element);
                if (type == "COLUMN_REF" && element.TryGetProperty("column_names", out var names) && names.GetArrayLength() == 2)
                {
                    var qualifier = names[0].GetString() ?? string.Empty;
                    var column = names[1].GetString() ?? string.Empty;
                    if (scope.TryGetValue(qualifier, out var table) && !table.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)))
                    {
                        problems.Add($"Column \"{column}\" does not exist on {table.Alias}.");
                    }
                }

                if (type == "JOIN")
                {
                    CheckJoin(element, scope, allowed, problems, used);
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, scope, allowed, problems, used);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, scope, allowed, problems, used);
                }

                break;
        }
    }

    private static void CheckJoin(JsonElement join, Dictionary<string, QueryTable> scope, IReadOnlyList<QueryJoin> allowed, List<string> problems, List<QueryJoin> used)
    {
        var refType = join.TryGetProperty("ref_type", out var r) ? r.GetString() : null;
        if (refType is "CROSS" or "NATURAL" or "ASOF" or "POSITIONAL")
        {
            problems.Add($"A {refType} join is not allowed: join with an explicit ON condition on one of the listed joins.");
            return;
        }

        if (join.TryGetProperty("using_columns", out var usingColumns) && usingColumns.ValueKind == JsonValueKind.Array && usingColumns.GetArrayLength() > 0)
        {
            problems.Add("JOIN ... USING is not allowed: write the ON condition with the table aliases, using one of the listed joins.");
            return;
        }

        if (!join.TryGetProperty("condition", out var condition) || condition.ValueKind != JsonValueKind.Object)
        {
            problems.Add("Every JOIN needs an ON condition that uses one of the listed joins.");
            return;
        }

        var pairs = new List<((string Alias, string Column) Left, (string Alias, string Column) Right)>();
        CollectEqualities(condition, pairs);
        if (pairs.Count == 0)
        {
            problems.Add("Every JOIN needs an ON condition of the form alias.column = alias.column using one of the listed joins.");
            return;
        }

        foreach (var (left, right) in pairs)
        {
            if (!scope.TryGetValue(left.Alias, out var lt) || !scope.TryGetValue(right.Alias, out var rt) || lt.Id == rt.Id)
            {
                continue; // a condition on something that is not a table pair (a literal, a subquery alias): nothing to verify here
            }

            var match = allowed.FirstOrDefault(a =>
                (a.FromTableId == lt.Id && a.ToTableId == rt.Id && Same(a.FromColumn, left.Column) && Same(a.ToColumn, right.Column))
                || (a.FromTableId == rt.Id && a.ToTableId == lt.Id && Same(a.FromColumn, right.Column) && Same(a.ToColumn, left.Column)));
            if (match is null)
            {
                problems.Add($"The join {lt.Alias}.{left.Column} = {rt.Alias}.{right.Column} is not a relationship in the knowledge graph. Use only the listed joins.");
            }
            else
            {
                used.Add(match);
            }
        }

        // A composite key means every pair together: a join that uses only part of it would multiply rows.
        foreach (var group in used.Where(u => u.GroupId is not null).GroupBy(u => u.GroupId).ToList())
        {
            var all = allowed.Where(a => a.GroupId == group.Key).ToList();
            if (all.Count > group.Count())
            {
                problems.Add($"This is a composite key of {all.Count} column pairs; join on all of them together: {string.Join(" AND ", all.Select(a => $"{a.FromColumn} = {a.ToColumn}"))}.");
            }
        }
    }

    private static void CollectEqualities(JsonElement node, List<((string, string), (string, string))> pairs)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (Type(node))
        {
            case "CONJUNCTION_AND" when node.TryGetProperty("children", out var children):
                foreach (var child in children.EnumerateArray())
                {
                    CollectEqualities(child, pairs);
                }

                break;
            case "COMPARE_EQUAL" when node.TryGetProperty("left", out var left) && node.TryGetProperty("right", out var right)
                && Column(left) is { } l && Column(right) is { } rr:
                pairs.Add((l, rr));
                break;
        }
    }

    private static (string Alias, string Column)? Column(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && Type(node) == "COLUMN_REF" && node.TryGetProperty("column_names", out var names) && names.GetArrayLength() == 2
            ? (names[0].GetString() ?? string.Empty, names[1].GetString() ?? string.Empty)
            : null;

    private static string? Type(JsonElement element) =>
        element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
