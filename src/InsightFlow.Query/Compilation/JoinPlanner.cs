using InsightFlow.Domain.Modeling;

namespace InsightFlow.Query.Compilation;

/// <summary>One join step: <c>LEFT JOIN Table ON FromTable.FromColumn = Table.ToColumn …</c>.</summary>
internal sealed record JoinStep(string FromTable, string Table, IReadOnlyList<(string FromColumn, string ToColumn)> Keys);

/// <summary>
/// Finds the shortest join path from the base table to every other table a spec touches, using the semantic
/// model's relationships in either direction. Joins are LEFT joins from the base (fact) side so filters on
/// dimensions never silently drop unmatched fact rows from totals unless they filter on those dimensions.
/// </summary>
internal static class JoinPlanner
{
    public static IReadOnlyList<JoinStep>? Plan(SemanticModel model, string baseTable, IEnumerable<string> requiredTables)
    {
        var edges = model.Relationships
            .SelectMany(r => new[]
            {
                (From: r.FromTable, To: r.ToTable, Keys: r.Keys.Select(k => (k.FromColumn, k.ToColumn)).ToList()),
                (From: r.ToTable, To: r.FromTable, Keys: r.Keys.Select(k => (k.ToColumn, k.FromColumn)).ToList()),
            })
            .ToLookup(e => e.From, StringComparer.OrdinalIgnoreCase);

        var steps = new List<JoinStep>();
        var joined = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseTable };

        foreach (var target in requiredTables.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (joined.Contains(target))
            {
                continue;
            }

            var path = ShortestPath(edges, baseTable, target);
            if (path is null)
            {
                return null;
            }

            foreach (var (from, to, keys) in path)
            {
                if (joined.Add(to))
                {
                    steps.Add(new JoinStep(from, to, keys));
                }
            }
        }

        return steps;
    }

    private static List<(string From, string To, List<(string, string)> Keys)>? ShortestPath(
        ILookup<string, (string From, string To, List<(string FromColumn, string ToColumn)> Keys)> edges,
        string start,
        string target)
    {
        var previous = new Dictionary<string, (string From, string To, List<(string, string)> Keys)>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var queue = new Queue<string>([start]);

        while (queue.TryDequeue(out var current))
        {
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            {
                var path = new List<(string, string, List<(string, string)>)>();
                for (var node = current; previous.TryGetValue(node, out var edge); node = edge.From)
                {
                    path.Add(edge);
                }

                path.Reverse();
                return path;
            }

            foreach (var edge in edges[current].Where(e => visited.Add(e.To)))
            {
                previous[edge.To] = edge;
                queue.Enqueue(edge.To);
            }
        }

        return null;
    }
}
