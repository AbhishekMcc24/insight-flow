using InsightFlow.Domain.Contracts.Playground;
using InsightFlow.Domain.Contracts.Query;

namespace InsightFlow.Web.Playground;

/// <summary>What a new element of each kind looks like: its size and its Excel-like default colours.</summary>
public static class ElementFactory
{
    private const string Blue = "#4472c4";
    private const string BlueEdge = "#2f528f";

    public static PlaygroundElementDto Create(string type, double x, double y, int z) => type switch
    {
        "text" => New(type, x, y, z, 220, 44, "Text", new PlaygroundStyleDto(FontSize: 16)),
        "title" => New(type, x, y, z, 420, 60, "Title", new PlaygroundStyleDto(FontSize: 32, Bold: true)),
        "note" => New(type, x, y, z, 190, 150, "Note", new PlaygroundStyleDto("#fff3a3", "#e6d36a", 1, "#3b3300", 15, Align: "left", VAlign: "top", Radius: 4)),
        "link" => New(type, x, y, z, 240, 36, "Link text", new PlaygroundStyleDto(Color: "#1d4ed8", FontSize: 16, Underline: true), url: "https://"),
        "rect" => Shape(type, x, y, z, 200, 110, 0),
        "rounded" => Shape(type, x, y, z, 200, 110, 18),
        "ellipse" => Shape(type, x, y, z, 200, 120, 0),
        "triangle" => Shape(type, x, y, z, 160, 140, 0),
        "diamond" => Shape(type, x, y, z, 170, 140, 0),
        "hexagon" => Shape(type, x, y, z, 190, 130, 0),
        "star" => Shape(type, x, y, z, 150, 150, 0),
        "line" => New(type, x, y, z, 220, 2, null, new PlaygroundStyleDto(null, BlueEdge, 2)),
        "arrow" => New(type, x, y, z, 220, 2, null, new PlaygroundStyleDto(null, BlueEdge, 2)),
        "image" => New(type, x, y, z, 260, 170, null, new PlaygroundStyleDto(null, "#cbd5e1", 0)),
        "kpi" => New(type, x, y, z, 220, 120, "Total sales\n1,234", new PlaygroundStyleDto("#ffffff", "#cbd5e1", 1, "#0f172a", 30, Bold: true, Align: "center", Radius: 10)),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown element type."),
    };

    private static PlaygroundElementDto Shape(string type, double x, double y, int z, double w, double h, double radius) =>
        New(type, x, y, z, w, h, null, new PlaygroundStyleDto(Blue, BlueEdge, 1, "#ffffff", 16, Align: "center", Radius: radius));

    private static PlaygroundElementDto New(string type, double x, double y, int z, double w, double h, string? text, PlaygroundStyleDto style, string? url = null) =>
        new(Guid.NewGuid(), type, x, y, w, h, z, text, style, Url: url);

    /// <summary>A chart or table made from an answer's rows, sized to fit them.</summary>
    public static PlaygroundElementDto FromResult(string kind, QueryResult result, ChartSpecDto? chart, string? caption, double x, double y, int z)
    {
        if (kind == "table")
        {
            var width = Math.Clamp(80 + (result.Columns.Count * 130), 320, 980);
            var height = Math.Clamp(70 + (Math.Min(result.Rows.Count, 12) * 30), 140, 460);
            return new PlaygroundElementDto(Guid.NewGuid(), "table", x, y, width, height, z, caption, new PlaygroundStyleDto(FontSize: 12), Data: result);
        }

        return new PlaygroundElementDto(Guid.NewGuid(), "chart", x, y, 620, 380, z, caption, new PlaygroundStyleDto(FontSize: 13), Chart: chart, Data: result);
    }
}

/// <summary>
/// The editing model of one sheet: its elements, what is selected, undo and redo, copy and paste, and ordering. Pure data and rules (no
/// browser), so every editing operation is unit-tested. The page asks it to change and then redraws; the browser script only reports
/// pointer gestures (move, resize), which arrive here as one change each.
/// </summary>
public sealed class PlaygroundBoard
{
    public const int MaxElements = PlaygroundElementRules.MaxElements;
    public const int MaxHistory = 60;
    public const double MinSize = 8;
    public const double GridSize = 12;

    private readonly Stack<List<PlaygroundElementDto>> _undo = new();
    private readonly Stack<List<PlaygroundElementDto>> _redo = new();
    private List<PlaygroundElementDto> _clipboard = [];
    private int _pasteCount;

    public List<PlaygroundElementDto> Elements { get; private set; } = [];

    public HashSet<Guid> Selected { get; } = [];

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool CanPaste => _clipboard.Count > 0;

    /// <summary>Raised whenever the elements change (so the page can save).</summary>
    public event Action? Changed;

    /// <summary>Replaces the sheet's content (opening a sheet): clears the selection and the history.</summary>
    public void Load(IEnumerable<PlaygroundElementDto> elements)
    {
        Elements = [.. elements];
        Selected.Clear();
        _undo.Clear();
        _redo.Clear();
    }

    public PlaygroundElementDto? Find(Guid id) => Elements.FirstOrDefault(e => e.Id == id);

    public IReadOnlyList<PlaygroundElementDto> SelectedElements => [.. Elements.Where(e => Selected.Contains(e.Id))];

    public int NextZ => Elements.Count == 0 ? 1 : Elements.Max(e => e.Z) + 1;

    public int LowestZ => Elements.Count == 0 ? 0 : Elements.Min(e => e.Z) - 1;

    // ----- selection ---------------------------------------------------------------------------------------------------------------

    public void Select(Guid? id, bool additive = false)
    {
        if (!additive)
        {
            Selected.Clear();
        }

        if (id is { } value && Find(value) is not null)
        {
            if (additive && !Selected.Add(value))
            {
                Selected.Remove(value);
            }
            else
            {
                Selected.Add(value);
            }
        }
    }

    public void SelectMany(IEnumerable<Guid> ids)
    {
        Selected.Clear();
        foreach (var id in ids.Where(i => Find(i) is not null))
        {
            Selected.Add(id);
        }
    }

    public void SelectAll()
    {
        Selected.Clear();
        foreach (var e in Elements)
        {
            Selected.Add(e.Id);
        }
    }

    // ----- editing -----------------------------------------------------------------------------------------------------------------

    public bool Add(PlaygroundElementDto element, bool select = true)
    {
        if (Elements.Count >= MaxElements)
        {
            return false;
        }

        Checkpoint();
        Elements.Add(element);
        if (select)
        {
            Selected.Clear();
            Selected.Add(element.Id);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Applies a change to every selected, unlocked element (style, text, order…).</summary>
    public void UpdateSelected(Func<PlaygroundElementDto, PlaygroundElementDto> change, bool includeLocked = false)
    {
        if (Selected.Count == 0)
        {
            return;
        }

        Checkpoint();
        Elements = [.. Elements.Select(e => Selected.Contains(e.Id) && (includeLocked || !e.Locked) ? change(e) : e)];
        Changed?.Invoke();
    }

    public void Update(Guid id, Func<PlaygroundElementDto, PlaygroundElementDto> change)
    {
        if (Find(id) is null)
        {
            return;
        }

        Checkpoint();
        Elements = [.. Elements.Select(e => e.Id == id ? change(e) : e)];
        Changed?.Invoke();
    }

    /// <summary>Commits what a drag, a resize or a nudge in the browser produced: new positions and sizes for some elements. One undo step.</summary>
    public void SetGeometry(IEnumerable<(Guid Id, double X, double Y, double W, double H)> changes)
    {
        var map = changes.Where(c => Find(c.Id) is { Locked: false }).ToDictionary(c => c.Id);
        if (map.Count == 0)
        {
            return;
        }

        Checkpoint();
        Elements = [.. Elements.Select(e => map.TryGetValue(e.Id, out var c)
            ? e with { X = Round(c.X), Y = Round(c.Y), W = Math.Max(MinSize, Round(c.W)), H = Math.Max(1, Round(c.H)) }
            : e)];
        Changed?.Invoke();
    }

    public void Nudge(double dx, double dy)
    {
        SetGeometry(SelectedElements.Where(e => !e.Locked).Select(e => (e.Id, e.X + dx, e.Y + dy, e.W, e.H)));
    }

    public void DeleteSelected()
    {
        if (Elements.All(e => !Selected.Contains(e.Id) || e.Locked))
        {
            return;
        }

        Checkpoint();
        Elements = [.. Elements.Where(e => !Selected.Contains(e.Id) || e.Locked)];
        Selected.RemoveWhere(id => Find(id) is null);
        Changed?.Invoke();
    }

    public void DuplicateSelected()
    {
        var copies = SelectedElements.Select(e => e with { Id = Guid.NewGuid(), X = e.X + 24, Y = e.Y + 24, Z = NextZ, Locked = false }).ToList();
        PlaceCopies(copies);
    }

    public void CopySelected() => _clipboard = [.. SelectedElements];

    public void CutSelected()
    {
        CopySelected();
        DeleteSelected();
    }

    public void Paste()
    {
        if (_clipboard.Count == 0)
        {
            return;
        }

        _pasteCount++;
        var offset = 24 * _pasteCount;
        PlaceCopies([.. _clipboard.Select((e, i) => e with { Id = Guid.NewGuid(), X = e.X + offset, Y = e.Y + offset, Z = NextZ + i, Locked = false })]);
    }

    private void PlaceCopies(List<PlaygroundElementDto> copies)
    {
        copies = [.. copies.Take(Math.Max(0, MaxElements - Elements.Count))];
        if (copies.Count == 0)
        {
            return;
        }

        Checkpoint();
        Elements.AddRange(copies);
        Selected.Clear();
        foreach (var c in copies)
        {
            Selected.Add(c.Id);
        }

        Changed?.Invoke();
    }

    // ----- order -------------------------------------------------------------------------------------------------------------------

    public void BringToFront()
    {
        var z = NextZ;
        var order = 0;
        UpdateSelected(e => e with { Z = z + order++ });
    }

    public void SendToBack()
    {
        var z = LowestZ - SelectedElements.Count;
        var order = 0;
        UpdateSelected(e => e with { Z = z + order++ });
    }

    // ----- alignment ---------------------------------------------------------------------------------------------------------------

    /// <summary>Aligns the selected elements to each other (<c>left</c>, <c>center</c>, <c>right</c>, <c>top</c>, <c>middle</c>, <c>bottom</c>).</summary>
    public void Align(string how)
    {
        var items = SelectedElements.Where(e => !e.Locked).ToList();
        if (items.Count < 2)
        {
            return;
        }

        var left = items.Min(e => e.X);
        var right = items.Max(e => e.X + e.W);
        var top = items.Min(e => e.Y);
        var bottom = items.Max(e => e.Y + e.H);
        SetGeometry(items.Select(e => how switch
        {
            "left" => (e.Id, left, e.Y, e.W, e.H),
            "right" => (e.Id, right - e.W, e.Y, e.W, e.H),
            "center" => (e.Id, ((left + right) / 2) - (e.W / 2), e.Y, e.W, e.H),
            "top" => (e.Id, e.X, top, e.W, e.H),
            "bottom" => (e.Id, e.X, bottom - e.H, e.W, e.H),
            "middle" => (e.Id, e.X, ((top + bottom) / 2) - (e.H / 2), e.W, e.H),
            _ => (e.Id, e.X, e.Y, e.W, e.H),
        }));
    }

    // ----- history -----------------------------------------------------------------------------------------------------------------

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(Elements);
        Elements = _undo.Pop();
        Selected.RemoveWhere(id => Find(id) is null);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(Elements);
        Elements = _redo.Pop();
        Selected.RemoveWhere(id => Find(id) is null);
        Changed?.Invoke();
    }

    /// <summary>Where to put the next element so it does not land exactly on the previous one.</summary>
    public (double X, double Y) NextSpot()
    {
        var n = Elements.Count % 10;
        return (48 + (n * 24), 48 + (n * 24));
    }

    private void Checkpoint()
    {
        _undo.Push([.. Elements]);
        _redo.Clear();
        if (_undo.Count > MaxHistory)
        {
            var keep = _undo.Take(MaxHistory).Reverse().ToList();
            _undo.Clear();
            foreach (var snapshot in keep)
            {
                _undo.Push(snapshot);
            }
        }
    }

    private static double Round(double value) => Math.Round(value, 1);
}
