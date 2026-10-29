using InsightFlow.Domain.Contracts.Knowledge;
using InsightFlow.Web.Charts;

namespace InsightFlow.Tests.Web;

public sealed class ErDiagramLayoutTests
{
    private static KnowledgeColumnDto Col(string name, bool key = false) => new(Guid.NewGuid(), name, "int", null, key, !key);

    private static KnowledgeTableDto Table(string name, params KnowledgeColumnDto[] columns) =>
        new(Guid.NewGuid(), null, null, "shop", "public", name, $"shop.public.{name}", null, "Database", true, columns);

    private static KnowledgeRelationshipDto Link(KnowledgeTableDto from, KnowledgeColumnDto fromColumn, KnowledgeTableDto to, KnowledgeColumnDto toColumn, string kind = "ForeignKey") =>
        new(Guid.NewGuid(), fromColumn.Id, toColumn.Id, from.DisplayName, fromColumn.Name, to.DisplayName, toColumn.Name, kind, 1, "test", "Manual");

    private static bool Overlap(ErCard a, ErCard b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    [Fact]
    public void Build_PointedAtTable_IsLeftOfTheTableThatPointsAtIt()
    {
        var customerId = Col("customer_id", key: true);
        var customers = Table("customers", customerId, Col("name"));
        var orderCustomer = Col("customer_id");
        var orders = Table("orders", Col("order_id", key: true), orderCustomer);

        var layout = ErDiagramLayout.Build([orders, customers], [Link(orders, orderCustomer, customers, customerId)]);

        layout.Cards.Single(c => c.TableId == customers.Id).X.ShouldBeLessThan(layout.Cards.Single(c => c.TableId == orders.Id).X);
    }

    [Fact]
    public void Build_Edge_StartsAndEndsOnTheLinkedColumnRows()
    {
        var customerId = Col("customer_id", key: true);
        var customers = Table("customers", Col("name"), customerId);
        var orderCustomer = Col("customer_id");
        var orders = Table("orders", Col("order_id", key: true), orderCustomer);
        var link = Link(orders, orderCustomer, customers, customerId);

        var layout = ErDiagramLayout.Build([orders, customers], [link]);

        var edge = layout.Edges.Single();
        var fromCard = layout.Cards.Single(c => c.TableId == orders.Id);
        var toCard = layout.Cards.Single(c => c.TableId == customers.Id);
        edge.Y1.ShouldBe(fromCard.Rows.Single(r => r.ColumnId == orderCustomer.Id).Y);
        edge.Y2.ShouldBe(toCard.Rows.Single(r => r.ColumnId == customerId.Id).Y);
        edge.X1.ShouldBe(fromCard.X);                 // leaves the left side of the table that points ...
        edge.X2.ShouldBe(toCard.X + toCard.Width);    // ... and arrives at the right side of the one pointed at
    }

    [Fact]
    public void Build_TwoRelationshipsBetweenTheSameTables_AreTwoSeparateLines()
    {
        var customerId = Col("customer_id", key: true);
        var regionId = Col("region_id");
        var customers = Table("customers", customerId, regionId);
        var orderCustomer = Col("customer_id");
        var orderRegion = Col("region_id");
        var orders = Table("orders", Col("order_id", key: true), orderCustomer, orderRegion);

        var layout = ErDiagramLayout.Build(
            [orders, customers],
            [Link(orders, orderCustomer, customers, customerId), Link(orders, orderRegion, customers, regionId, "SameAttribute")]);

        layout.Edges.Count.ShouldBe(2);
        layout.Edges[0].Path.ShouldNotBe(layout.Edges[1].Path);
        layout.Edges[0].Y1.ShouldNotBe(layout.Edges[1].Y1);
        layout.Edges[0].Y2.ShouldNotBe(layout.Edges[1].Y2);
    }

    [Fact]
    public void Build_ShowsLinkedAndKeyColumns_AndCountsTheRest()
    {
        var customerId = Col("customer_id", key: true);
        var customers = Table("customers", customerId, Col("a"), Col("b"), Col("c"));
        var orderCustomer = Col("customer_id");
        var orders = Table("orders", Col("x"), Col("order_id", key: true), orderCustomer, Col("y"));

        var layout = ErDiagramLayout.Build([orders, customers], [Link(orders, orderCustomer, customers, customerId)]);

        var ordersCard = layout.Cards.Single(c => c.TableId == orders.Id);
        ordersCard.Rows.Select(r => r.Name).ShouldBe(["x", "order_id", "customer_id", "y"]); // few columns: all are shown, in table order
        ordersCard.Rows.Single(r => r.Name == "customer_id").IsLinked.ShouldBeTrue();
        ordersCard.Rows.Single(r => r.Name == "order_id").IsKey.ShouldBeTrue();

        var wide = Table("wide", [.. Enumerable.Range(0, 30).Select(i => Col($"c{i}", key: i == 0))]);
        var wideLink = wide.Columns[25];
        var layout2 = ErDiagramLayout.Build([wide, customers], [Link(wide, wideLink, customers, customerId)]);
        var wideCard = layout2.Cards.Single(c => c.TableId == wide.Id);
        wideCard.Rows.Count.ShouldBe(ErDiagramLayout.MaxRows);
        wideCard.HiddenColumns.ShouldBe(30 - ErDiagramLayout.MaxRows);
        wideCard.Rows.Select(r => r.Name).ShouldContain("c25"); // the linked column is always visible, however far down it is
    }

    [Fact]
    public void Build_TablesWithoutRelationships_GoInABlockBelow_AndNothingOverlaps()
    {
        var customerId = Col("customer_id", key: true);
        var customers = Table("customers", customerId);
        var orderCustomer = Col("customer_id");
        var orders = Table("orders", orderCustomer);
        var loose = Enumerable.Range(0, 7).Select(i => Table($"loose{i}", Col("id", key: true), Col("name"))).ToList();

        var layout = ErDiagramLayout.Build([orders, customers, .. loose], [Link(orders, orderCustomer, customers, customerId)]);

        layout.UnlinkedTop.ShouldNotBeNull();
        var linkedBottom = layout.Cards.Where(c => c.IsLinked).Max(c => c.Y + c.Height);
        layout.Cards.Where(c => !c.IsLinked).ShouldAllBe(c => c.Y >= linkedBottom);
        layout.Cards.Count.ShouldBe(9);
        for (var i = 0; i < layout.Cards.Count; i++)
        {
            for (var j = i + 1; j < layout.Cards.Count; j++)
            {
                Overlap(layout.Cards[i], layout.Cards[j]).ShouldBeFalse($"{layout.Cards[i].Title} overlaps {layout.Cards[j].Title}");
            }
        }

        layout.Cards.ShouldAllBe(c => c.X + c.Width <= layout.Width && c.Y + c.Height <= layout.Height);
    }

    [Fact]
    public void Build_ChainOfTables_NeverOverlapsAndKeepsEveryEdge()
    {
        var tables = Enumerable.Range(0, 8).Select(i => Table($"t{i}", Col("id", key: true), Col("parent_id"))).ToList();
        var links = Enumerable.Range(1, 7).Select(i => Link(tables[i], tables[i].Columns[1], tables[i - 1], tables[i - 1].Columns[0])).ToList();

        var layout = ErDiagramLayout.Build(tables, links);

        layout.Edges.Count.ShouldBe(7);
        layout.Cards.Select(c => c.X).Distinct().Count().ShouldBe(3); // eight levels fold into three columns instead of one long row
        for (var i = 0; i < layout.Cards.Count; i++)
        {
            for (var j = i + 1; j < layout.Cards.Count; j++)
            {
                Overlap(layout.Cards[i], layout.Cards[j]).ShouldBeFalse();
            }
        }
    }

    [Fact]
    public void Build_TablesThatPointAtEachOther_FormACompactBlock_NotOneLongRow()
    {
        // Six tables that all relate to each other in both directions (as in a real order-to-cash database).
        var tables = Enumerable.Range(0, 6).Select(i => Table($"t{i}", Col("id", key: true), Col("ref_id"))).ToList();
        var links = new List<KnowledgeRelationshipDto>();
        foreach (var from in tables)
        {
            foreach (var to in tables.Where(t => t != from))
            {
                links.Add(Link(from, from.Columns[1], to, to.Columns[0]));
            }
        }

        var layout = ErDiagramLayout.Build(tables, links);

        layout.Cards.Select(c => c.X).Distinct().Count().ShouldBeLessThanOrEqualTo(3);
        layout.Edges.Count.ShouldBe(links.Count); // every relationship is still drawn
        layout.Width.ShouldBeLessThan(3 * (ErDiagramLayout.CardWidth + ErDiagramLayout.GapX) + 100);
    }

    [Fact]
    public void Build_CycleOfRelationships_StillLaysOutAndIsDeterministic()
    {
        var a = Table("a", Col("id", key: true), Col("b_id"));
        var b = Table("b", Col("id", key: true), Col("a_id"));
        var links = new[] { Link(a, a.Columns[1], b, b.Columns[0]), Link(b, b.Columns[1], a, a.Columns[0]) };

        var first = ErDiagramLayout.Build([a, b], links);
        var second = ErDiagramLayout.Build([a, b], links);

        first.Cards.Count.ShouldBe(2);
        first.Edges.Count.ShouldBe(2);
        first.Cards.Select(c => (c.TableId, c.X, c.Y)).ShouldBe(second.Cards.Select(c => (c.TableId, c.X, c.Y)));
        first.Edges.Select(e => e.Path).ShouldBe(second.Edges.Select(e => e.Path));
    }

    [Fact]
    public void Build_SelfReferenceAndDanglingRelationship_DoNotBreakTheLayout()
    {
        var id = Col("id", key: true);
        var parent = Col("parent_id");
        var tree = Table("tree", id, parent);
        var dangling = new KnowledgeRelationshipDto(Guid.NewGuid(), Guid.NewGuid(), id.Id, "x", "y", "tree", "id", "ForeignKey", 1, "", "Manual");

        var layout = ErDiagramLayout.Build([tree], [Link(tree, parent, tree, id), dangling]);

        layout.Cards.Count.ShouldBe(1);
        layout.Edges.Count.ShouldBe(1); // the self-reference loops back on the card; the dangling one is dropped
        layout.Edges[0].Path.ShouldStartWith("M ");
    }

    [Fact]
    public void Build_ExpandedWideTable_ShowsEveryColumn()
    {
        var customerId = Col("customer_id", key: true);
        var customers = Table("customers", customerId);
        var wide = Table("wide", [.. Enumerable.Range(0, 30).Select(i => Col($"c{i}", key: i == 0))]);
        var links = new[] { Link(wide, wide.Columns[3], customers, customerId) };

        var collapsed = ErDiagramLayout.Build([wide, customers], links).Cards.Single(c => c.TableId == wide.Id);
        var expanded = ErDiagramLayout.Build([wide, customers], links, new HashSet<Guid> { wide.Id }).Cards.Single(c => c.TableId == wide.Id);

        collapsed.IsExpanded.ShouldBeFalse();
        expanded.IsExpanded.ShouldBeTrue();
        expanded.Rows.Count.ShouldBe(30);
        expanded.HiddenColumns.ShouldBe(0);
        expanded.Height.ShouldBeGreaterThan(collapsed.Height);
    }

    [Fact]
    public void Build_Edge_HasAMidpointBetweenItsEnds()
    {
        var customerId = Col("customer_id", key: true);
        var customers = Table("customers", customerId);
        var orderCustomer = Col("customer_id");
        var orders = Table("orders", orderCustomer);

        var edge = ErDiagramLayout.Build([orders, customers], [Link(orders, orderCustomer, customers, customerId)]).Edges.Single();

        edge.MidX.ShouldBeInRange(Math.Min(edge.X1, edge.X2), Math.Max(edge.X1, edge.X2));
        edge.MidY.ShouldBeInRange(Math.Min(edge.Y1, edge.Y2), Math.Max(edge.Y1, edge.Y2));
    }

    [Fact]
    public void Build_CollapsedLinkedTable_KeepsOnlyItsLinkedColumns()
    {
        var customerId = Col("customer_id", key: true);
        var customers = Table("customers", customerId, Col("a"), Col("b"));
        var orderCustomer = Col("customer_id");
        var orders = Table("orders", Col("order_id", key: true), orderCustomer, Col("x"));
        var link = Link(orders, orderCustomer, customers, customerId);

        var open = ErDiagramLayout.Build([orders, customers], [link]).Cards.Single(c => c.TableId == orders.Id);
        var layout = ErDiagramLayout.Build([orders, customers], [link], collapsed: new HashSet<Guid> { orders.Id });
        var card = layout.Cards.Single(c => c.TableId == orders.Id);

        card.IsCollapsed.ShouldBeTrue();
        card.Rows.Select(r => r.Name).ShouldBe(["customer_id"]);
        card.Height.ShouldBeLessThan(open.Height);
        layout.Edges.Single().Y1.ShouldBe(card.Rows.Single().Y); // the line still ends on its column
    }

    [Fact]
    public void Build_CollapsedUnlinkedTable_IsJustAHeader()
    {
        var loose = Table("loose", Col("id", key: true), Col("name"));

        var card = ErDiagramLayout.Build([loose], [], collapsed: new HashSet<Guid> { loose.Id }).Cards.Single();

        card.Rows.ShouldBeEmpty();
        card.Height.ShouldBe(ErDiagramLayout.HeaderHeight + 6);
    }

    [Fact]
    public void Build_MovedCard_MovesItsRowsAndReroutesItsLines()
    {
        var customerId = Col("customer_id", key: true);
        var customers = Table("customers", customerId);
        var orderCustomer = Col("customer_id");
        var orders = Table("orders", orderCustomer);
        var link = Link(orders, orderCustomer, customers, customerId);
        var before = ErDiagramLayout.Build([orders, customers], [link]);

        var after = ErDiagramLayout.Build([orders, customers], [link], offsets: new Dictionary<Guid, (double, double)> { [orders.Id] = (100, 200) });

        var movedBefore = before.Cards.Single(c => c.TableId == orders.Id);
        var moved = after.Cards.Single(c => c.TableId == orders.Id);
        moved.X.ShouldBe(movedBefore.X + 100);
        moved.Rows.Single().Y.ShouldBe(movedBefore.Rows.Single().Y + 200);
        after.Edges.Single().Y1.ShouldBe(moved.Rows.Single().Y);
        after.Edges.Single().Path.ShouldNotBe(before.Edges.Single().Path);
        after.Cards.ShouldAllBe(c => c.X + c.Width <= after.Width && c.Y + c.Height <= after.Height);
    }

    [Fact]
    public void Build_CardDraggedOffTheTopLeft_IsPushedBackOntoTheCanvas()
    {
        var a = Table("a", Col("id", key: true));

        var layout = ErDiagramLayout.Build([a], [], offsets: new Dictionary<Guid, (double, double)> { [a.Id] = (-500, -500) });

        layout.Cards.Single().X.ShouldBeGreaterThanOrEqualTo(ErDiagramLayout.Margin);
        layout.Cards.Single().Y.ShouldBeGreaterThanOrEqualTo(ErDiagramLayout.Margin);
    }

    [Fact]
    public void Build_Scrollable_ShowsAWindowOfColumnsAndPinsLinkedOnesOutOfView()
    {
        var customerId = Col("customer_id", key: true);
        var customers = Table("customers", customerId);
        var wide = Table("wide", [.. Enumerable.Range(0, 40).Select(i => Col($"c{i}", key: i == 0))]);
        var deep = wide.Columns[30]; // below the first window
        var link = Link(wide, deep, customers, customerId);

        var top = ErDiagramLayout.Build([wide, customers], [link], scrollable: true).Cards.Single(c => c.TableId == wide.Id);
        top.IsScrollable.ShouldBeTrue();
        top.Rows.Count.ShouldBe(ErDiagramLayout.ViewRows);
        top.TotalRows.ShouldBe(40);
        top.LinkedIndexes.ShouldBe([30]);
        top.Pinned!.Single().ColumnId.ShouldBe(deep.Id);
        top.Pinned!.Single().Y.ShouldBeGreaterThan(top.Rows[^1].Y); // pinned to the bottom edge while out of view

        var scrolled = ErDiagramLayout.Build([wide, customers], [link], scrollable: true, scroll: new Dictionary<Guid, int> { [wide.Id] = 25 }).Cards.Single(c => c.TableId == wide.Id);
        scrolled.FirstRow.ShouldBe(25);
        scrolled.Rows.Select(r => r.Name).ShouldContain("c30");
        scrolled.Pinned.ShouldBeEmpty();
    }

    [Fact]
    public void Build_ScrollPastTheEnd_IsClamped_AndEdgesStillEndOnTheCard()
    {
        var a = Table("a", [.. Enumerable.Range(0, 20).Select(i => Col($"a{i}"))]);
        var b = Table("b", Col("id", key: true));

        var layout = ErDiagramLayout.Build([a, b], [Link(a, a.Columns[0], b, b.Columns[0])], scrollable: true, scroll: new Dictionary<Guid, int> { [a.Id] = 999 });

        var card = layout.Cards.Single(c => c.TableId == a.Id);
        card.FirstRow.ShouldBe(20 - ErDiagramLayout.ViewRows);
        layout.Edges.Single().Y1.ShouldBeInRange(card.Y, card.Y + card.Height); // the first column is scrolled away; its line sits on the card's top edge
    }

    private static Dictionary<Guid, (double X, double Y)> Positions(ErLayout layout) => layout.Cards.ToDictionary(c => c.TableId, c => (c.X, c.Y));

    [Fact]
    public void Build_WithFixedPositions_AddingARelationshipMovesNoTable()
    {
        var a = Table("a", Col("id", key: true), Col("b_id"));
        var b = Table("b", Col("id", key: true), Col("c_id"));
        var c = Table("c", Col("id", key: true));
        var first = ErDiagramLayout.Build([a, b, c], [Link(a, a.Columns[1], b, b.Columns[0])], fixedPositions: new Dictionary<Guid, (double, double)>());
        var pins = Positions(first);

        // A new relationship that would, on its own, put the tables in different layers.
        var second = ErDiagramLayout.Build(
            [a, b, c], [Link(a, a.Columns[1], b, b.Columns[0]), Link(b, b.Columns[1], c, c.Columns[0])], fixedPositions: pins);

        var after = Positions(second);
        after.Count.ShouldBe(pins.Count);
        foreach (var (id, at) in pins)
        {
            after[id].ShouldBe(at);
        }

        second.Edges.Count.ShouldBe(2); // the new line is there, drawn between tables that stayed where they were
    }

    [Fact]
    public void Build_WithFixedPositions_ANewTableGoesInANewColumnBesideTheOthers()
    {
        var a = Table("a", Col("id", key: true));
        var b = Table("b", Col("id", key: true));
        var pins = Positions(ErDiagramLayout.Build([a], [], fixedPositions: new Dictionary<Guid, (double, double)>()));

        var layout = ErDiagramLayout.Build([a, b], [], fixedPositions: pins);

        var old = layout.Cards.Single(c => c.TableId == a.Id);
        var fresh = layout.Cards.Single(c => c.TableId == b.Id);
        (old.X, old.Y).ShouldBe(pins[a.Id]);
        fresh.X.ShouldBeGreaterThanOrEqualTo(old.X + old.Width + ErDiagramLayout.GapX);
        layout.UnlinkedTop.ShouldBeNull();
    }

    [Fact]
    public void Build_AutoLayout_KeepsRoomBetweenTables()
    {
        var tables = Enumerable.Range(0, 6).Select(i => Table($"t{i}", Col("id", key: true), Col("parent_id"))).ToList();
        var links = Enumerable.Range(1, 5).Select(i => Link(tables[i], tables[i].Columns[1], tables[i - 1], tables[i - 1].Columns[0])).ToList();
        var extra = Link(tables[3], tables[3].Columns[1], tables[1], tables[1].Columns[0]);

        var layout = ErDiagramLayout.Build(tables, [.. links, extra]);

        for (var i = 0; i < layout.Cards.Count; i++)
        {
            for (var j = i + 1; j < layout.Cards.Count; j++)
            {
                var (p, q) = (layout.Cards[i], layout.Cards[j]);
                var sameColumn = Math.Abs(p.X - q.X) < 1;
                var gap = sameColumn ? Math.Max(q.Y - (p.Y + p.Height), p.Y - (q.Y + q.Height)) : Math.Max(q.X - (p.X + p.Width), p.X - (q.X + q.Width));
                gap.ShouldBeGreaterThanOrEqualTo(sameColumn ? ErDiagramLayout.GapY - 1 : ErDiagramLayout.GapX - 1);
            }
        }
    }

    [Fact]
    public void Build_NoTables_IsAnEmptyButValidLayout()
    {
        var layout = ErDiagramLayout.Build([], []);

        layout.Cards.ShouldBeEmpty();
        layout.Edges.ShouldBeEmpty();
        layout.Width.ShouldBeGreaterThanOrEqualTo(0);
        layout.Height.ShouldBeGreaterThan(0);
    }
}
