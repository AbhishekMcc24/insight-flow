namespace InsightFlow.Domain.Knowledge;

/// <summary>
/// Pulls the words that matter out of a plain-English question, for matching against the names and descriptions in the knowledge
/// graph. Pure, so it is unit-tested; the matching itself runs in Neo4j.
/// </summary>
public static class QuestionTokens
{
    public const int MaxTokens = 12;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "what", "which", "who", "whom", "how", "many", "much", "are", "was", "were", "has", "have",
        "had", "does", "did", "show", "give", "list", "tell", "get", "find", "all", "any", "each", "per", "by", "of", "in", "on", "at", "to", "me", "my",
        "our", "their", "than", "then", "there", "these", "those", "into", "over", "under", "between", "total", "number", "count", "average", "sum",
        "top", "last", "first", "most", "least", "more", "less", "can", "could", "would", "should", "please", "about", "where", "when", "also", "not",
    };

    /// <summary>Lower-case words of 3+ characters (letters and digits), without common filler words, each once, in order, at most <see cref="MaxTokens"/>.</summary>
    public static IReadOnlyList<string> Extract(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in question.Length > 2_000 ? question[..2_000] : question)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length >= 3)
            {
                var word = current.ToString();
                if (!Stop.Contains(word) && !tokens.Contains(word) && tokens.Count < MaxTokens)
                {
                    tokens.Add(word);
                }
            }

            current.Clear();
        }
    }
}

/// <summary>A relationship between two tables as the graph stores it (one column pair); pairs of a composite key share <see cref="GroupId"/>.</summary>
public sealed record JoinEdge(
    Guid FromTable, string FromColumn, Guid ToTable, string ToColumn, RelationshipKind Kind, double Confidence, Guid? GroupId = null);

/// <summary>The tables a question needs, in the order they were found, and which of the wanted tables could not be connected to the others.</summary>
public sealed record JoinConnection(IReadOnlyList<Guid> Tables, IReadOnlyList<Guid> Bridges, IReadOnlyList<Guid> Unconnected);

/// <summary>
/// Finds how a set of tables can be joined using the relationships in the knowledge graph. Starting from the best-matching table, it
/// walks the shortest route to each of the others over relationships (a foreign key is a cheap hop, a "same attribute" link an
/// expensive one, so it is used only when nothing else connects the tables) and adds the tables along the way as bridges. A relationship on
/// an audit column (<see cref="AuditColumns"/>: created_user, modified_by…) only says who wrote a row, so it is very expensive and can end a
/// route (to reach a user table that was asked for) but never carry one through a table: two payments tables are not "related" because the same
/// person created rows in both. A link between two audit columns ("same creator") is not a route at all.
/// </summary>
public static class JoinPaths
{
    public const int MaxHops = 3;

    /// <summary>Cost of an audit-column hop: far above any ordinary route (three hops cost at most 12), so it is only ever a last resort.</summary>
    public const double AuditCost = 25.0;

    public static JoinConnection Connect(IReadOnlyList<Guid> wanted, IReadOnlyList<JoinEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(edges);
        var seeds = wanted.Distinct().ToList();
        if (seeds.Count == 0)
        {
            return new JoinConnection([], [], []);
        }

        var neighbours = new Dictionary<Guid, List<(Guid To, double Cost, bool Audit)>>();
        foreach (var e in edges.Where(e => e.FromTable != e.ToTable))
        {
            // "Same creator" (an audit column on both sides) relates no rows to each other: never a route.
            if (AuditColumns.IsAuditColumn(e.FromColumn) && AuditColumns.IsAuditColumn(e.ToColumn))
            {
                continue;
            }

            var audit = AuditColumns.IsAuditRelationship(e.FromColumn, e.ToColumn);
            var cost = audit ? AuditCost : e.Kind switch
            {
                RelationshipKind.ForeignKey => 1.0,
                RelationshipKind.DerivedFrom => 4.0,
                _ => 3.0,
            };
            Add(e.FromTable, e.ToTable, cost, audit);
            Add(e.ToTable, e.FromTable, cost, audit);
        }

        var included = new List<Guid> { seeds[0] };
        var bridges = new List<Guid>();
        var unconnected = new List<Guid>();
        foreach (var target in seeds.Skip(1))
        {
            if (included.Contains(target))
            {
                continue;
            }

            // Cheapest route from anything already included to the next wanted table.
            var path = included.Select(start => Cheapest(start, target)).Where(p => p is not null).OrderBy(p => p!.Cost).FirstOrDefault();
            if (path is null)
            {
                unconnected.Add(target);
                continue;
            }

            foreach (var step in path.Steps)
            {
                if (!included.Contains(step))
                {
                    included.Add(step);
                    if (!seeds.Contains(step))
                    {
                        bridges.Add(step);
                    }
                }
            }
        }

        return new JoinConnection(included, bridges, unconnected);

        void Add(Guid from, Guid to, double cost, bool audit)
        {
            if (!neighbours.TryGetValue(from, out var list))
            {
                neighbours[from] = list = [];
            }

            list.Add((to, cost, audit));
        }

        Route? Cheapest(Guid start, Guid goal)
        {
            // Dijkstra over a handful of tables, limited to MaxHops hops.
            var best = new Dictionary<Guid, (double Cost, int Hops, Guid? Prev, bool ViaAudit)> { [start] = (0, 0, null, false) };
            var open = new List<Guid> { start };
            while (open.Count > 0)
            {
                var current = open.OrderBy(n => best[n].Cost).First();
                open.Remove(current);
                if (current == goal)
                {
                    break;
                }

                var (cost, hops, _, viaAudit) = best[current];
                if (hops >= MaxHops || !neighbours.TryGetValue(current, out var next))
                {
                    continue;
                }

                foreach (var (to, step, audit) in next)
                {
                    // An audit link may end a route but never be passed through: it says who wrote a row, not how two tables relate.
                    if (current != start && (audit || viaAudit))
                    {
                        continue;
                    }

                    var total = cost + step;
                    if (!best.TryGetValue(to, out var known) || total < known.Cost)
                    {
                        best[to] = (total, hops + 1, current, audit);
                        if (!open.Contains(to))
                        {
                            open.Add(to);
                        }
                    }
                }
            }

            if (!best.TryGetValue(goal, out var reached))
            {
                return null;
            }

            var steps = new List<Guid>();
            for (Guid? at = goal; at is not null; at = best[at.Value].Prev)
            {
                steps.Add(at.Value);
            }

            steps.Reverse();
            return new Route(reached.Cost, steps);
        }
    }

    private sealed record Route(double Cost, List<Guid> Steps);
}
