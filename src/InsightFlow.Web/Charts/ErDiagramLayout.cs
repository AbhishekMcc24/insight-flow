using System.Globalization;
using InsightFlow.Contracts.Knowledge;

namespace InsightFlow.Web.Charts;

/// <summary>A column row inside a table card. <see cref="Y"/> is the vertical centre of the row in diagram coordinates.</summary>
public sealed record ErRow(Guid ColumnId, string Name, string Type, bool IsKey, bool IsLinked, double Y);

/// <summary>A table drawn as a card: a header and the columns that matter (linked and key columns), with a count of the rest.</summary>
public sealed record ErCard(
    Guid TableId,
    string Title,
    string Subtitle,
    string Source,
    double X,
    double Y,
    double Width,
    double Height,
    IReadOnlyList<ErRow> Rows,
    int HiddenColumns,
    bool IsLinked,
    bool IsExpanded = false,
    bool IsCollapsed = false,
    IReadOnlyList<ErRow>? Pinned = null,
    int TotalRows = 0,
    int FirstRow = 0,
    IReadOnlyList<int>? LinkedIndexes = null,
    bool IsScrollable = false);

/// <summary>A relationship drawn from one column row to another. <see cref="Path"/> is an SVG cubic Bézier.</summary>
public sealed record ErEdge(
    Guid RelationshipId,
    Guid FromTableId,
    Guid ToTableId,
    Guid FromColumnId,
    Guid ToColumnId,
    string Kind,
    double Confidence,
    string Tooltip,
    double X1,
    double Y1,
    double X2,
    double Y2,
    string Path,
    double MidX = 0,
    double MidY = 0,
    Guid? GroupId = null);

public sealed record ErLayout(double Width, double Height, IReadOnlyList<ErCard> Cards, IReadOnlyList<ErEdge> Edges, double? UnlinkedTop);

/// <summary>
/// Lays the knowledge base out as an entity-relationship diagram. Tables are arranged in columns so that a table that is
/// pointed at sits to the left of the tables that point at it; inside a column, tables are ordered to keep lines short. Each
/// relationship starts and ends on the exact column rows it links, so two relationships between the same two tables are two
/// separate lines, and no text is needed on the lines. Tables with no relationships are placed in a block underneath.
/// Pure and deterministic, so it is unit-tested.
/// </summary>
public static class ErDiagramLayout
{
    public const double CardWidth = 280;
    public const double HeaderHeight = 48;
    public const double RowHeight = 30;
    public const double CardPadding = 8;
    public const double GapX = 240;
    public const double GapY = 72;

    /// <summary>Space between the cards of the "not linked yet" block.</summary>
    public const double UnlinkedGap = 64;
    public const double Margin = 32;
    public const int MaxRows = 12;
    public const int UnlinkedRows = 5;

    /// <summary>In the scrolling (interactive) view, how many column rows a card shows at once.</summary>
    public const int ViewRows = 12;

    /// <param name="tables">Every table in the knowledge base.</param>
    /// <param name="relationships">Every relationship.</param>
    /// <param name="expanded">Tables whose card shows every column instead of the linked and key ones (used by the interactive builder).</param>
    /// <param name="collapsed">Tables folded down to their linked columns only (a table with no links folds to just its header).</param>
    /// <param name="scrollable">Interactive view: each card shows a window of <see cref="ViewRows"/> columns that can be scrolled, instead of a fixed pick of the important ones.</param>
    /// <param name="scroll">For each table, the index of the first column in its window.</param>
    /// <param name="fixedPositions">Where each table already sits (top-left corner). Those tables stay exactly there, so a new relationship never moves them; tables not listed yet go in a new column to the right. Null: lay everything out automatically (and keep the "not linked yet" label).</param>
    /// <param name="offsets">Where the person dragged cards to, as a shift from the automatic position, so the layout keeps its shape.</param>
    public static ErLayout Build(
        IReadOnlyList<KnowledgeTableDto> tables,
        IReadOnlyList<KnowledgeRelationshipDto> relationships,
        IReadOnlySet<Guid>? expanded = null,
        IReadOnlySet<Guid>? collapsed = null,
        IReadOnlyDictionary<Guid, (double Dx, double Dy)>? offsets = null,
        bool scrollable = false,
        IReadOnlyDictionary<Guid, int>? scroll = null,
        IReadOnlyDictionary<Guid, (double X, double Y)>? fixedPositions = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(relationships);

        var tableByColumn = tables.SelectMany(t => t.Columns.Select(c => (c.Id, Table: t))).ToDictionary(x => x.Id, x => x.Table);
        var usable = relationships
            .Where(r => tableByColumn.ContainsKey(r.FromColumnId) && tableByColumn.ContainsKey(r.ToColumnId))
            .ToList();
        var linkedColumns = usable.SelectMany(r => new[] { r.FromColumnId, r.ToColumnId }).ToHashSet();
        var linkedTables = usable.SelectMany(r => new[] { tableByColumn[r.FromColumnId].Id, tableByColumn[r.ToColumnId].Id }).ToHashSet();

        // Scrolling view: every card shows a window onto all of its columns (or only its linked ones when folded).
        var windows = new Dictionary<Guid, Window>();
        if (scrollable)
        {
            foreach (var t in tables)
            {
                var all = collapsed?.Contains(t.Id) == true ? OnlyLinked(t, linkedColumns) : [.. t.Columns];
                var visible = Math.Min(ViewRows, all.Count);
                var start = Math.Clamp(scroll?.GetValueOrDefault(t.Id) ?? 0, 0, Math.Max(0, all.Count - visible));
                windows[t.Id] = new Window(all, start, [.. all.Skip(start).Take(visible)]);
            }
        }

        var connected = tables.Where(t => linkedTables.Contains(t.Id)).OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var isolated = tables.Where(t => !linkedTables.Contains(t.Id)).OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        var layers = AssignLayers(connected, usable, tableByColumn);
        var order = OrderWithinLayers(connected, usable, tableByColumn, layers);

        // Build cards (heights first, so each layer can be centred against the tallest one).
        var cardsByTable = new Dictionary<Guid, ErCard>();
        var layerHeights = new Dictionary<int, double>();
        var shells = new Dictionary<Guid, (KnowledgeTableDto Table, List<KnowledgeColumnDto> Rows, int Hidden, double Height)>();
        foreach (var table in connected)
        {
            var fold = collapsed?.Contains(table.Id) == true;
            var rows = windows.TryGetValue(table.Id, out var win) ? win.Visible : fold ? OnlyLinked(table, linkedColumns) : PickRows(table, linkedColumns, expanded?.Contains(table.Id) == true ? int.MaxValue : MaxRows);
            var hidden = table.Columns.Count - rows.Count;
            shells[table.Id] = (table, rows, hidden, CardHeight(rows.Count, !fold && !scrollable && HasFooter(table, linked: true), fold));
        }

        foreach (var layer in order.Keys)
        {
            layerHeights[layer] = order[layer].Sum(id => shells[id].Height) + GapY * (order[layer].Count - 1);
        }

        var tallest = layerHeights.Count == 0 ? 0 : layerHeights.Values.Max();
        foreach (var (layer, ids) in order.OrderBy(kv => kv.Key))
        {
            var x = Margin + layer * (CardWidth + GapX);
            var y = Margin + (tallest - layerHeights[layer]) / 2;
            foreach (var id in ids)
            {
                var s = shells[id];
                cardsByTable[id] = MakeCard(s.Table, s.Rows, s.Hidden, linkedColumns, x, y, isLinked: true, expanded?.Contains(id) == true, collapsed?.Contains(id) == true, windows.GetValueOrDefault(id));
                y += s.Height + GapY;
            }
        }

        var layerCount = order.Count == 0 ? 0 : order.Keys.Max() + 1;
        var mainWidth = layerCount == 0 ? 0 : Margin * 2 + layerCount * CardWidth + (layerCount - 1) * GapX;
        var mainHeight = layerCount == 0 ? 0 : Margin * 2 + tallest;

        // Tables with no relationships yet sit in a block below, several to a row.
        double? unlinkedTop = null;
        var cards = cardsByTable.Values.ToList();
        var width = Math.Max(mainWidth, Margin * 2 + CardWidth);
        var height = mainHeight;
        if (isolated.Count > 0)
        {
            unlinkedTop = mainHeight == 0 ? Margin : mainHeight + 8;
            var perRow = Math.Max(1, (int)Math.Floor((Math.Max(mainWidth, Margin * 2 + 3 * CardWidth + 2 * UnlinkedGap) - Margin * 2 + UnlinkedGap) / (CardWidth + UnlinkedGap)));
            var y = unlinkedTop.Value + 40;
            for (var i = 0; i < isolated.Count; i += perRow)
            {
                var rowTables = isolated.Skip(i).Take(perRow).ToList();
                var rowShells = rowTables.Select(t =>
                {
                    var fold = collapsed?.Contains(t.Id) == true;
                    var rows = windows.TryGetValue(t.Id, out var win) ? win.Visible : fold ? OnlyLinked(t, linkedColumns) : PickRows(t, linkedColumns, expanded?.Contains(t.Id) == true ? int.MaxValue : UnlinkedRows);
                    return (Table: t, Rows: rows, Hidden: t.Columns.Count - rows.Count, Fold: fold);
                }).ToList();
                var rowHeight = rowShells.Max(s => CardHeight(s.Rows.Count, !s.Fold && !scrollable && HasFooter(s.Table, linked: false), s.Fold));
                for (var j = 0; j < rowShells.Count; j++)
                {
                    var s = rowShells[j];
                    cards.Add(MakeCard(s.Table, s.Rows, s.Hidden, linkedColumns, Margin + j * (CardWidth + UnlinkedGap), y, isLinked: false, expanded?.Contains(s.Table.Id) == true, s.Fold, windows.GetValueOrDefault(s.Table.Id)));
                }

                y += rowHeight + GapY;
                width = Math.Max(width, Margin * 2 + rowShells.Count * CardWidth + (rowShells.Count - 1) * UnlinkedGap);
            }

            height = y - GapY + Margin;
        }

        if (fixedPositions is not null && fixedPositions.Count > 0)
        {
            var settled = cards.Where(c => fixedPositions.ContainsKey(c.TableId)).ToList();
            var rightEdge = settled.Count == 0 ? Margin - GapX : settled.Max(c => fixedPositions[c.TableId].X + c.Width);
            var nextY = Margin;
            var placed = new List<ErCard>(cards.Count);
            foreach (var c in cards)
            {
                if (fixedPositions.TryGetValue(c.TableId, out var p))
                {
                    placed.Add(Shift(c, p.X - c.X, p.Y - c.Y));
                }
                else
                {
                    // A table that was not there before: a new column beside everything that is, so nothing is moved or covered.
                    placed.Add(Shift(c, rightEdge + GapX - c.X, nextY - c.Y));
                    nextY += c.Height + GapY;
                }
            }

            cards = placed;
        }

        if ((fixedPositions is not null || offsets is { Count: > 0 }) && cards.Count > 0)
        {
            cards = [.. cards.Select(c => offsets is not null && offsets.TryGetValue(c.TableId, out var o) ? Shift(c, o.Dx, o.Dy) : c)];
            // Keep every moved card on the canvas: nothing may sit left of / above the margin, and the canvas grows to hold the rest.
            var minX = cards.Min(c => c.X);
            var minY = cards.Min(c => c.Y);
            var pushX = minX < Margin ? Margin - minX : 0;
            var pushY = minY < Margin ? Margin - minY : 0;
            if (pushX > 0 || pushY > 0)
            {
                cards = [.. cards.Select(c => Shift(c, pushX, pushY))];
                unlinkedTop += pushY;
            }

            width = Math.Max(width + pushX, cards.Max(c => c.X + c.Width) + Margin);
            height = Math.Max(height + pushY, cards.Max(c => c.Y + c.Height) + Margin);
        }

        var rowLookup = cards.SelectMany(c => c.Rows.Concat(c.Pinned ?? []).Select(r => (r.ColumnId, Card: c, Row: r))).ToDictionary(x => x.ColumnId, x => x);
        var edges = BuildEdges(usable, tableByColumn, rowLookup);
        // With remembered positions the "not linked yet" block is wherever the cards were left, so its label has no place of its own.
        return new ErLayout(width, Math.Max(height, 120), cards, edges, fixedPositions is null ? unlinkedTop : null);
    }

    private static ErCard Shift(ErCard c, double dx, double dy) =>
        c with { X = c.X + dx, Y = c.Y + dy, Rows = [.. c.Rows.Select(r => r with { Y = r.Y + dy })], Pinned = c.Pinned is null ? null : [.. c.Pinned.Select(r => r with { Y = r.Y + dy })] };

    /// <summary>A folded card keeps only the columns that are in a relationship, so a line never loses its end.</summary>
    internal static List<KnowledgeColumnDto> OnlyLinked(KnowledgeTableDto table, HashSet<Guid> linkedColumns) =>
        [.. table.Columns.Where(c => linkedColumns.Contains(c.Id))];

    /// <summary>
    /// A pointed-at table is left of the tables that point at it. Tables that point at each other (a cycle) would otherwise be pushed ever
    /// further right, so the relationships that close a cycle are ignored for placement only (they are still drawn). The columns of cards
    /// are then folded to at most <see cref="MaxColumns"/> (more for very many tables), so a well-connected set of tables forms a compact
    /// block, not one long row.
    /// </summary>
    internal static Dictionary<Guid, int> AssignLayers(
        List<KnowledgeTableDto> connected, List<KnowledgeRelationshipDto> relationships, Dictionary<Guid, KnowledgeTableDto> tableByColumn)
    {
        var edges = relationships
            .Select(r => (From: tableByColumn[r.FromColumnId].Id, To: tableByColumn[r.ToColumnId].Id))
            .Where(e => e.From != e.To)
            .Distinct()
            .ToList();
        var outgoing = connected.ToDictionary(t => t.Id, _ => new List<Guid>());
        foreach (var (from, to) in edges)
        {
            outgoing[from].Add(to);
        }

        // Cut cycles: a depth-first walk (tables in name order, so the result is stable) drops every edge that points back into the walk.
        var kept = connected.ToDictionary(t => t.Id, _ => new List<Guid>());
        var state = connected.ToDictionary(t => t.Id, _ => 0); // 0 new, 1 on the walk, 2 done
        foreach (var start in connected)
        {
            Visit(start.Id);
        }

        void Visit(Guid id)
        {
            if (state[id] != 0)
            {
                return;
            }

            state[id] = 1;
            foreach (var next in outgoing[id])
            {
                if (state[next] == 1)
                {
                    continue; // closes a cycle
                }

                kept[id].Add(next);
                Visit(next);
            }

            state[id] = 2;
        }

        // Longest path to a table that points at nothing (kept edges form a DAG).
        var layer = new Dictionary<Guid, int>();
        int Depth(Guid id)
        {
            if (layer.TryGetValue(id, out var known))
            {
                return known;
            }

            return layer[id] = kept[id].Count == 0 ? 0 : kept[id].Max(Depth) + 1;
        }

        foreach (var t in connected)
        {
            Depth(t.Id);
        }

        var columns = Math.Max(MaxColumns, (int)Math.Ceiling(Math.Sqrt(connected.Count)));
        return layer.ToDictionary(kv => kv.Key, kv => kv.Value % columns);
    }

    /// <summary>Most columns of cards before the layout folds back, for a typical knowledge base.</summary>
    public const int MaxColumns = 3;


    /// <summary>Orders the tables in every layer by the average position of their neighbours, which keeps most lines short and uncrossed.</summary>
    internal static SortedDictionary<int, List<Guid>> OrderWithinLayers(
        List<KnowledgeTableDto> connected,
        List<KnowledgeRelationshipDto> relationships,
        Dictionary<Guid, KnowledgeTableDto> tableByColumn,
        Dictionary<Guid, int> layers)
    {
        var order = new SortedDictionary<int, List<Guid>>();
        foreach (var t in connected)
        {
            if (!order.TryGetValue(layers[t.Id], out var list))
            {
                order[layers[t.Id]] = list = [];
            }

            list.Add(t.Id);
        }

        var neighbours = connected.ToDictionary(t => t.Id, _ => new List<Guid>());
        foreach (var r in relationships)
        {
            var a = tableByColumn[r.FromColumnId].Id;
            var b = tableByColumn[r.ToColumnId].Id;
            if (a != b)
            {
                neighbours[a].Add(b);
                neighbours[b].Add(a);
            }
        }

        for (var sweep = 0; sweep < 4; sweep++)
        {
            foreach (var key in order.Keys.ToList())
            {
                var position = order.Values.SelectMany(l => l.Select((id, i) => (id, i))).ToDictionary(x => x.id, x => (double)x.i);
                order[key] = [.. order[key]
                    .Select((id, i) => (Id: id, Score: neighbours[id].Count == 0 ? i : neighbours[id].Average(n => position[n]), Index: i))
                    .OrderBy(x => x.Score).ThenBy(x => x.Index)
                    .Select(x => x.Id)];
            }
        }

        return order;
    }

    /// <summary>Columns worth showing: the ones in a relationship first, then keys, then the rest in table order, up to <paramref name="max"/>.</summary>
    internal static List<KnowledgeColumnDto> PickRows(KnowledgeTableDto table, HashSet<Guid> linkedColumns, int max)
    {
        var picked = new HashSet<Guid>();
        foreach (var c in table.Columns.Where(c => linkedColumns.Contains(c.Id)).Take(max))
        {
            picked.Add(c.Id);
        }

        foreach (var c in table.Columns.Where(c => c.IsPrimaryKey))
        {
            if (picked.Count < max)
            {
                picked.Add(c.Id);
            }
        }

        foreach (var c in table.Columns)
        {
            if (picked.Count < max)
            {
                picked.Add(c.Id);
            }
        }

        return [.. table.Columns.Where(c => picked.Contains(c.Id))];
    }

    /// <summary>A card has a last row ("+ N more columns" or "Show fewer") when the table has more columns than its default card shows.</summary>
    internal static bool HasFooter(KnowledgeTableDto table, bool linked) => table.Columns.Count > (linked ? MaxRows : UnlinkedRows);

    private static double CardHeight(int rows, bool footer, bool folded = false) =>
        folded && rows == 0 ? HeaderHeight + 6 : HeaderHeight + CardPadding + (rows + (footer ? 1 : 0)) * RowHeight + CardPadding;

    /// <summary>A card's window onto its columns in the scrolling view: all of them, where the window starts, and the ones in view.</summary>
    private sealed record Window(List<KnowledgeColumnDto> All, int Start, List<KnowledgeColumnDto> Visible);

    private static ErCard MakeCard(
        KnowledgeTableDto table, List<KnowledgeColumnDto> rows, int hidden, HashSet<Guid> linkedColumns, double x, double y, bool isLinked, bool isExpanded, bool isCollapsed, Window? window = null)
    {
        var top = y + HeaderHeight + CardPadding;
        var erRows = rows
            .Select((c, i) => new ErRow(c.Id, c.Name, c.DataType, c.IsPrimaryKey, linkedColumns.Contains(c.Id), top + i * RowHeight + RowHeight / 2))
            .ToList();

        // Linked columns scrolled out of view still need somewhere for their lines to attach: the top or bottom edge of the rows area.
        List<ErRow>? pinned = null;
        List<int>? linkedIndexes = null;
        if (window is not null)
        {
            pinned = [];
            linkedIndexes = [];
            var bottom = top + rows.Count * RowHeight;
            for (var i = 0; i < window.All.Count; i++)
            {
                var c = window.All[i];
                if (!linkedColumns.Contains(c.Id))
                {
                    continue;
                }

                linkedIndexes.Add(i);
                if (i < window.Start)
                {
                    pinned.Add(new ErRow(c.Id, c.Name, c.DataType, c.IsPrimaryKey, true, top + 3));
                }
                else if (i >= window.Start + rows.Count)
                {
                    pinned.Add(new ErRow(c.Id, c.Name, c.DataType, c.IsPrimaryKey, true, bottom - 3));
                }
            }
        }

        var subtitle = string.Join(" · ", new[] { table.DatabaseName, table.SchemaName }.Where(p => !string.IsNullOrEmpty(p)));
        var showFooter = !isCollapsed && window is null && HasFooter(table, isLinked);
        return new ErCard(
            table.Id, table.Name, string.IsNullOrEmpty(subtitle) ? table.Source : subtitle, table.Source, x, y, CardWidth,
            CardHeight(rows.Count, showFooter, isCollapsed), erRows, window is null ? hidden : window.All.Count - rows.Count, isLinked, isExpanded, isCollapsed,
            pinned, window?.All.Count ?? 0, window?.Start ?? 0, linkedIndexes, window is not null);
    }

    private static List<ErEdge> BuildEdges(
        List<KnowledgeRelationshipDto> relationships,
        Dictionary<Guid, KnowledgeTableDto> tableByColumn,
        Dictionary<Guid, (Guid ColumnId, ErCard Card, ErRow Row)> rows)
    {
        var edges = new List<ErEdge>();
        var sameLayerLoops = new Dictionary<Guid, int>();
        foreach (var r in relationships)
        {
            if (!rows.TryGetValue(r.FromColumnId, out var from) || !rows.TryGetValue(r.ToColumnId, out var to))
            {
                continue;
            }

            double x1, x2, c1, c2;
            string path;
            var y1 = from.Row.Y;
            var y2 = to.Row.Y;
            var gap = Math.Abs(from.Card.X - to.Card.X) - CardWidth; // clear space between the two cards (negative when they overlap sideways)
            if (gap < 40)
            {
                // Stacked or close together: leave and re-enter on the right, looping a little further out for each extra line.
                var n = sameLayerLoops[from.Card.TableId] = sameLayerLoops.GetValueOrDefault(from.Card.TableId) + 1;
                x1 = from.Card.X + CardWidth;
                x2 = to.Card.X + CardWidth;
                var reach = Math.Max(x1, x2) + 36 + 14 * n;
                (c1, c2) = (reach, reach);
                path = $"M {F(x1)} {F(y1)} C {F(c1)} {F(y1)}, {F(c2)} {F(y2)}, {F(x2)} {F(y2)}";
            }
            else if (from.Card.X > to.Card.X)
            {
                // The pointing table is to the right: leave its left edge, arrive at the other's right edge.
                x1 = from.Card.X;
                x2 = to.Card.X + CardWidth;
                var d = Math.Max(50, (x1 - x2) / 2);
                (c1, c2) = (x1 - d, x2 + d);
                path = $"M {F(x1)} {F(y1)} C {F(c1)} {F(y1)}, {F(c2)} {F(y2)}, {F(x2)} {F(y2)}";
            }
            else
            {
                x1 = from.Card.X + CardWidth;
                x2 = to.Card.X;
                var d = Math.Max(50, (x2 - x1) / 2);
                (c1, c2) = (x1 + d, x2 - d);
                path = $"M {F(x1)} {F(y1)} C {F(c1)} {F(y1)}, {F(c2)} {F(y2)}, {F(x2)} {F(y2)}";
            }

            var tooltip = $"{tableByColumn[r.FromColumnId].DisplayName}.{r.FromColumn} → {tableByColumn[r.ToColumnId].DisplayName}.{r.ToColumn} · {r.Kind} · {r.Confidence:P0}"
                + (string.IsNullOrWhiteSpace(r.Reason) ? "" : $" · {r.Reason}");
            edges.Add(new ErEdge(
                r.Id, from.Card.TableId, to.Card.TableId, r.FromColumnId, r.ToColumnId, r.Kind, r.Confidence, tooltip, x1, y1, x2, y2, path,
                (x1 + 3 * c1 + 3 * c2 + x2) / 8, (y1 + y2) / 2, r.GroupId));
        }

        return edges;
    }

    private static string F(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
