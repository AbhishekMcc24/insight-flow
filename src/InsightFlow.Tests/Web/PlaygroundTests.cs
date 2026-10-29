using InsightFlow.Domain.Contracts.Playground;
using InsightFlow.Domain.Contracts.Query;
using InsightFlow.Web.Playground;

namespace InsightFlow.Tests.Web;

/// <summary>The Playground board edits elements in memory; the server rules decide what may be stored.</summary>
public sealed class PlaygroundTests
{
    private static PlaygroundElementDto Box(double x = 0, double y = 0, int z = 1) => ElementFactory.Create("rect", x, y, z);

    [Fact]
    public void Add_NewElement_IsSelectedAndUndoable()
    {
        var board = new PlaygroundBoard();
        var el = Box();

        board.Add(el).ShouldBeTrue();

        board.Elements.Count.ShouldBe(1);
        board.Selected.ShouldContain(el.Id);
        board.Undo();
        board.Elements.ShouldBeEmpty();
        board.Redo();
        board.Elements.Count.ShouldBe(1);
    }

    [Fact]
    public void Add_PastTheLimit_IsRefused()
    {
        var board = new PlaygroundBoard();
        for (var i = 0; i < PlaygroundBoard.MaxElements; i++)
        {
            board.Add(Box(i, 0, i)).ShouldBeTrue();
        }

        board.Add(Box()).ShouldBeFalse();
        board.Elements.Count.ShouldBe(PlaygroundBoard.MaxElements);
    }

    [Fact]
    public void SetGeometry_MovesAndResizes()
    {
        var board = new PlaygroundBoard();
        var el = Box();
        board.Add(el);

        board.SetGeometry([(el.Id, 100, 50, 200, 80)]);

        var moved = board.Find(el.Id)!;
        (moved.X, moved.Y, moved.W, moved.H).ShouldBe((100, 50, 200, 80));
    }

    [Fact]
    public void DuplicateSelected_MakesAnotherElementWithANewId()
    {
        var board = new PlaygroundBoard();
        var el = Box();
        board.Add(el);

        board.DuplicateSelected();

        board.Elements.Count.ShouldBe(2);
        board.Elements.Select(e => e.Id).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void CopyThenPaste_AddsACopy()
    {
        var board = new PlaygroundBoard();
        board.Add(Box());

        board.CopySelected();
        board.Paste();

        board.Elements.Count.ShouldBe(2);
    }

    [Fact]
    public void CutThenPaste_KeepsOneElement()
    {
        var board = new PlaygroundBoard();
        board.Add(Box());

        board.CutSelected();
        board.Elements.ShouldBeEmpty();
        board.Paste();

        board.Elements.Count.ShouldBe(1);
    }

    [Fact]
    public void DeleteSelected_RemovesIt()
    {
        var board = new PlaygroundBoard();
        board.Add(Box());

        board.DeleteSelected();

        board.Elements.ShouldBeEmpty();
        board.Selected.ShouldBeEmpty();
    }

    [Fact]
    public void BringToFront_PutsTheElementAboveTheRest()
    {
        var board = new PlaygroundBoard();
        var a = Box(0, 0, 1);
        var b = Box(10, 10, 2);
        board.Add(a);
        board.Add(b);
        board.Select(a.Id);

        board.BringToFront();

        board.Find(a.Id)!.Z.ShouldBeGreaterThan(board.Find(b.Id)!.Z);
    }

    [Fact]
    public void Align_Left_GivesSelectedElementsTheSameX()
    {
        var board = new PlaygroundBoard();
        var a = Box(40, 0, 1);
        var b = Box(300, 100, 2);
        board.Add(a);
        board.Add(b);
        board.SelectAll();

        board.Align("left");

        board.Find(a.Id)!.X.ShouldBe(board.Find(b.Id)!.X);
        board.Find(a.Id)!.X.ShouldBe(40);
    }

    [Fact]
    public void Update_LockedElement_StillAcceptsTextButNotGeometry()
    {
        var board = new PlaygroundBoard();
        var el = Box();
        board.Add(el);
        board.UpdateSelected(e => e with { Locked = true }, includeLocked: true);

        board.SetGeometry([(el.Id, 500, 500, 10, 10)]);

        board.Find(el.Id)!.X.ShouldBe(el.X);
    }

    [Fact]
    public void Create_EveryKnownType_ProducesThatType()
    {
        foreach (var type in PlaygroundElementTypes.All.Where(t => t is not ("chart" or "table")))
        {
            ElementFactory.Create(type, 0, 0, 1).Type.ShouldBe(type);
        }
    }

    [Fact]
    public void FromResult_Table_CarriesTheRows()
    {
        var result = new QueryResult([new QueryColumn("a", ColumnType.String)], [new object?[] { "x" }], false);

        var el = ElementFactory.FromResult("table", result, null, "Sales", 0, 0, 1);

        el.Type.ShouldBe("table");
        el.Data.ShouldBe(result);
        el.Text.ShouldBe("Sales");
    }

    [Fact]
    public void Sanitize_UnknownTypeAndBadUrls_AreDropped()
    {
        var good = Box();
        var bad = good with { Id = Guid.NewGuid(), Type = "script" };
        var link = ElementFactory.Create("link", 0, 0, 1) with { Url = "javascript:alert(1)" };
        var mail = ElementFactory.Create("link", 0, 0, 1) with { Url = "mailto:a@b.com" };

        var result = PlaygroundElementRules.Sanitize([good, bad, link, mail]);

        result.Count.ShouldBe(3);
        result.ShouldNotContain(e => e.Type == "script");
        result.Single(e => e.Id == link.Id).Url.ShouldBeNull();
        result.Single(e => e.Id == mail.Id).Url.ShouldBe("mailto:a@b.com");
    }

    [Fact]
    public void Sanitize_BadColourAndHugeNumbers_AreClamped()
    {
        var el = Box() with { X = 1e12, W = -5, Style = new PlaygroundStyleDto(Fill: "red;background:url(x)", Opacity: 9, FontSize: 5000) };

        var clean = PlaygroundElementRules.Sanitize([el]).Single();

        clean.X.ShouldBeLessThanOrEqualTo(20_000);
        clean.W.ShouldBeGreaterThanOrEqualTo(1);
        clean.Style!.Fill.ShouldBeNull();
        clean.Style.Opacity.ShouldBe(1);
        clean.Style.FontSize.ShouldBeLessThanOrEqualTo(200);
    }

    [Fact]
    public void Sanitize_MoreThanTheLimit_KeepsTheLimit()
    {
        var many = Enumerable.Range(0, PlaygroundElementRules.MaxElements + 20).Select(i => Box(i, 0, i)).ToList();

        PlaygroundElementRules.Sanitize(many).Count.ShouldBe(PlaygroundElementRules.MaxElements);
    }
}

/// <summary>The Excel export keeps the canvas layout: tables start at their own cell, everything else is a shape or picture at its pixel position.</summary>
public sealed class PlaygroundXlsxTests
{
    private static QueryResult Rows(int rows) =>
        new([new QueryColumn("name", ColumnType.String), new QueryColumn("total", ColumnType.Number)], [.. Enumerable.Range(0, rows).Select(i => (IReadOnlyList<object?>)new object?[] { "A & B <x>", i + 0.5 })], false);

    private static string Entry(System.IO.Compression.ZipArchive zip, string path)
    {
        using var reader = new StreamReader(zip.GetEntry(path)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void Build_TableTitleAndChart_KeepsPositionsAndIsWellFormed()
    {
        var table = ElementFactory.FromResult("table", Rows(2), null, "Sales", 128, 100, 1);
        var title = ElementFactory.Create("title", 0, 0, 2) with { Text = "Report" };
        var chart = ElementFactory.FromResult("chart", Rows(2), null, "Chart", 700, 40, 3);

        var bytes = PlaygroundXlsx.Build([("Revenue", [table, title, chart]), ("Empty", [])], new Dictionary<Guid, byte[]> { [chart.Id] = [137, 80, 78, 71] });

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith(".png", StringComparison.Ordinal)))
        {
            using var stream = entry.Open();
            System.Xml.Linq.XDocument.Load(stream).ShouldNotBeNull();
        }

        var sheet = Entry(zip, "xl/worksheets/sheet1.xml");
        sheet.ShouldContain("r=\"C6\"");
        sheet.ShouldContain("A &amp; B &lt;x&gt;");
        sheet.ShouldContain("<v>0.5</v>");
        var drawing = Entry(zip, "xl/drawings/drawing1.xml");
        drawing.ShouldContain("Report");
        drawing.ShouldContain("<xdr:pic>");
        zip.GetEntry("xl/media/image1.png").ShouldNotBeNull();
    }

    [Fact]
    public void Layout_TallTable_PushesElementsBelowItDown()
    {
        var table = ElementFactory.FromResult("table", Rows(50), null, null, 0, 0, 1) with { H = 200 };
        var below = ElementFactory.Create("text", 0, 300, 2);

        var layout = PlaygroundXlsx.Layout([table, below]);

        layout.Placed.Single().Y.ShouldBe(300 + ((51 * PlaygroundXlsx.RowPixels) - 200));
    }

    [Fact]
    public void UniqueNames_BadOrRepeatedNames_AreMadeValid()
    {
        var names = PlaygroundXlsx.UniqueNames(["Q1/Q2", "q1q2", new string('x', 50), ""]);

        names[0].ShouldBe("Q1Q2");
        names[1].ShouldBe("q1q2 (2)");
        names[2].Length.ShouldBeLessThanOrEqualTo(31);
        names[3].ShouldBe("Sheet");
    }
}

/// <summary>A table can keep its header and first columns in view; the Excel export freezes the same rows and columns.</summary>
public sealed class PlaygroundFreezeTests
{
    private static QueryResult Data() =>
        new([new QueryColumn("a", ColumnType.String), new QueryColumn("b", ColumnType.Number)], [new object?[] { "x", 1.0 }], false);

    [Fact]
    public void Sanitize_FreezeColumns_IsClampedAndKept()
    {
        var table = ElementFactory.FromResult("table", Data(), null, null, 0, 0, 1) with { FreezeHeader = false, FreezeColumns = 99 };

        var clean = PlaygroundElementRules.Sanitize([table]).Single();

        clean.FreezeHeader.ShouldBeFalse();
        clean.FreezeColumns.ShouldBe(5);
    }

    [Fact]
    public void Layout_FrozenHeaderAndColumn_SetsThePane()
    {
        var table = ElementFactory.FromResult("table", Data(), null, "Caption", 128, 100, 1) with { FreezeColumns = 1 };

        var pane = PlaygroundXlsx.Layout([table]).Pane;

        // Table at C6: caption on row 6, header on row 7, so rows 1-7 and columns A-C stay.
        pane.ShouldNotBeNull();
        (pane.Value.Rows, pane.Value.Columns).ShouldBe((7, 3));
    }

    [Fact]
    public void Layout_NothingFrozen_HasNoPane()
    {
        var table = ElementFactory.FromResult("table", Data(), null, null, 0, 0, 1) with { FreezeHeader = false };

        PlaygroundXlsx.Layout([table]).Pane.ShouldBeNull();
    }
}
