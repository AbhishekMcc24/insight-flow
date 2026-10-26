using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using InsightFlow.Contracts.Playground;
using InsightFlow.Contracts.Query;

namespace InsightFlow.Web.Playground;

/// <summary>
/// Writes the Playground as an Excel workbook (one worksheet per sheet) without any package: an .xlsx file is a zip of a few XML parts.
/// The sheet keeps its layout. Everything is placed at the pixel position it has on the canvas: text boxes, titles, notes, KPI cards, shapes
/// and lines become real Excel shapes, charts and images become pictures (the browser draws the chart, so it looks exactly as on the canvas),
/// and a table becomes cells starting at the cell under its top-left corner. A table with more rows than its box pushes what is below it down
/// by the extra rows, so nothing is drawn over the data.
/// </summary>
public static class PlaygroundXlsx
{
    public const double ColumnPixels = 64;
    public const double RowPixels = 20;

    private const int Normal = 0;
    private const int Bold = 1;
    private const int Header = 2;
    private const int MaxName = 31;
    private const double Emu = 9525;

    public sealed record Cell(string? Text, double? Number, int Style);

    /// <summary>One sheet worked out: its cells, column widths, and the other elements with their final pixel positions.</summary>
    public sealed class SheetLayout
    {
        public Dictionary<(int Row, int Col), Cell> Cells { get; } = [];

        public Dictionary<int, double> ColumnWidths { get; } = [];

        public List<(PlaygroundElementDto Element, double X, double Y)> Placed { get; } = [];

        /// <summary>Rows and columns to freeze (Excel freezes the sheet, not a table, so the topmost table that asks for it decides).</summary>
        public (int Rows, int Columns, double Y)? Pane { get; set; }
    }

    public static byte[] Build(IReadOnlyList<(string Name, IReadOnlyList<PlaygroundElementDto> Elements)> sheets, IReadOnlyDictionary<Guid, byte[]>? images = null)
    {
        images ??= new Dictionary<Guid, byte[]>();
        var names = UniqueNames([.. sheets.Select(s => s.Name)]);
        var count = Math.Max(1, sheets.Count);
        var media = new List<byte[]>();
        var drawings = new List<int>();
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < count; i++)
            {
                var elements = i < sheets.Count ? sheets[i].Elements : [];
                var layout = Layout(elements);
                var pictures = new List<(string Rel, int Media)>();
                var drawing = Drawing(layout, images, media, pictures);
                if (drawing is not null)
                {
                    drawings.Add(i + 1);
                    Add(zip, $"xl/drawings/drawing{i + 1}.xml", drawing);
                    Add(zip, $"xl/drawings/_rels/drawing{i + 1}.xml.rels", DrawingRels(pictures));
                    Add(zip, $"xl/worksheets/_rels/sheet{i + 1}.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing{i + 1}.xml\"/></Relationships>");
                }

                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", Worksheet(layout, drawing is not null));
            }

            Add(zip, "[Content_Types].xml", ContentTypes(count, drawings));
            Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml", Workbook(names, count));
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(count));
            Add(zip, "xl/styles.xml", Styles);
            for (var m = 0; m < media.Count; m++)
            {
                var entry = zip.CreateEntry($"xl/media/image{m + 1}.png", CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(media[m]);
            }
        }

        return output.ToArray();
    }

    // ───────────── layout: where everything goes ─────────────

    public static SheetLayout Layout(IReadOnlyList<PlaygroundElementDto> elements)
    {
        var layout = new SheetLayout();
        var tables = elements.Where(e => e.Type == "table" && e.Data is not null).ToList();

        // A table with more rows than its box is taller in cells than on the canvas; everything that starts below it moves down by the difference.
        double ExtraOf(PlaygroundElementDto t) => Math.Max(0, TableRows(t) * RowPixels - t.H);
        double Shift(PlaygroundElementDto e) => tables.Where(t => t.Id != e.Id && t.Y + t.H <= e.Y + 1).Sum(ExtraOf);

        foreach (var e in elements.OrderBy(e => e.Z))
        {
            var x = Math.Max(0, e.X);
            var y = Math.Max(0, e.Y + Shift(e));
            if (e.Type == "table" && e.Data is { } data)
            {
                PutTable(layout, e, data, (int)Math.Floor(y / RowPixels), (int)Math.Floor(x / ColumnPixels));
            }
            else
            {
                layout.Placed.Add((e, x, y));
            }
        }

        return layout;
    }

    private static int TableRows(PlaygroundElementDto t) => (string.IsNullOrWhiteSpace(t.Text) ? 0 : 1) + 1 + (t.Data?.Rows.Count ?? 0);

    private static void PutTable(SheetLayout layout, PlaygroundElementDto e, QueryResult data, int row, int col)
    {
        if (!string.IsNullOrWhiteSpace(e.Text))
        {
            layout.Cells[(row, col)] = new Cell(e.Text, null, Bold);
            row++;
        }

        var frozenColumns = Math.Min(e.FreezeColumns, data.Columns.Count);
        if ((e.FreezeHeader || frozenColumns > 0) && (layout.Pane is null || e.Y < layout.Pane.Value.Y))
        {
            layout.Pane = (e.FreezeHeader ? row + 1 : 0, frozenColumns > 0 ? col + frozenColumns : 0, e.Y);
        }

        for (var c = 0; c < data.Columns.Count; c++)
        {
            layout.Cells[(row, col + c)] = new Cell(data.Columns[c].Name, null, Header);
            Widen(layout, col + c, data.Columns[c].Name.Length);
        }

        foreach (var values in data.Rows)
        {
            row++;
            for (var c = 0; c < data.Columns.Count; c++)
            {
                var cell = ToCell(c < values.Count ? values[c] : null);
                layout.Cells[(row, col + c)] = cell;
                Widen(layout, col + c, cell.Text?.Length ?? cell.Number?.ToString("0.##", CultureInfo.InvariantCulture).Length ?? 0);
            }
        }
    }

    private static void Widen(SheetLayout layout, int column, int characters)
    {
        var px = Math.Clamp((characters * 7) + 18, ColumnPixels, 320);
        layout.ColumnWidths[column] = layout.ColumnWidths.TryGetValue(column, out var old) ? Math.Max(old, px) : px;
    }

    private static Cell ToCell(object? value) => value switch
    {
        null => new Cell(null, null, Normal),
        JsonElement { ValueKind: JsonValueKind.Null } => new Cell(null, null, Normal),
        JsonElement { ValueKind: JsonValueKind.Number } n when n.TryGetDouble(out var d) => new Cell(null, d, Normal),
        JsonElement j => new Cell(j.ToString(), null, Normal),
        double d => new Cell(null, d, Normal),
        decimal m => new Cell(null, (double)m, Normal),
        int or long or short or byte or float => new Cell(null, Convert.ToDouble(value, CultureInfo.InvariantCulture), Normal),
        IFormattable f => new Cell(f.ToString(null, CultureInfo.InvariantCulture), null, Normal),
        _ => new Cell(Convert.ToString(value, CultureInfo.InvariantCulture), null, Normal),
    };

    // ───────────── names ─────────────

    public static List<string> UniqueNames(IReadOnlyList<string> names)
    {
        var result = new List<string>();
        foreach (var raw in names)
        {
            var clean = new string([.. raw.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\'))]).Trim().Trim('\'');
            if (clean.Length == 0)
            {
                clean = "Sheet";
            }

            clean = clean.Length > MaxName ? clean[..MaxName] : clean;
            var name = clean;
            for (var n = 2; result.Contains(name, StringComparer.OrdinalIgnoreCase); n++)
            {
                var suffix = $" ({n})";
                name = (clean.Length + suffix.Length > MaxName ? clean[..(MaxName - suffix.Length)] : clean) + suffix;
            }

            result.Add(name);
        }

        if (result.Count == 0)
        {
            result.Add("Sheet 1");
        }

        return result;
    }

    // ───────────── worksheet ─────────────

    private static string Worksheet(SheetLayout layout, bool hasDrawing)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        if (layout.Pane is { } pane)
        {
            // Excel freezes everything above and left of one cell; a header that sits lower on the sheet freezes the rows above it as well.
            var active = pane is { Rows: > 0, Columns: > 0 } ? "bottomRight" : pane.Rows > 0 ? "bottomLeft" : "topRight";
            var split = (pane.Columns > 0 ? $" xSplit=\"{pane.Columns}\"" : string.Empty) + (pane.Rows > 0 ? $" ySplit=\"{pane.Rows}\"" : string.Empty);
            sb.Append(CultureInfo.InvariantCulture, $"<sheetViews><sheetView workbookViewId=\"0\"><pane{split} topLeftCell=\"{Column(pane.Columns)}{pane.Rows + 1}\" activePane=\"{active}\" state=\"frozen\"/><selection pane=\"{active}\"/></sheetView></sheetViews>");
        }
        else
        {
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
        }
        sb.Append("<sheetFormatPr baseColWidth=\"8\" defaultColWidth=\"9.140625\" defaultRowHeight=\"15\"/>");
        if (layout.ColumnWidths.Count > 0)
        {
            sb.Append("<cols>");
            foreach (var (col, px) in layout.ColumnWidths.OrderBy(c => c.Key))
            {
                sb.Append(CultureInfo.InvariantCulture, $"<col min=\"{col + 1}\" max=\"{col + 1}\" width=\"{(px / 7.0).ToString("0.######", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            }

            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");
        foreach (var row in layout.Cells.GroupBy(c => c.Key.Row).OrderBy(g => g.Key))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{row.Key + 1}\">");
            foreach (var item in row.OrderBy(x => x.Key.Col))
            {
                var cell = item.Value;
                var reference = $"{Column(item.Key.Col)}{item.Key.Row + 1}";
                if (cell.Number is { } number)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" s=\"{cell.Style}\"><v>{number.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                }
                else if (!string.IsNullOrEmpty(cell.Text))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" s=\"{cell.Style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(cell.Text)}</t></is></c>");
                }
            }

            sb.Append("</row>");
        }

        sb.Append("</sheetData>");
        if (hasDrawing)
        {
            sb.Append("<drawing r:id=\"rId1\"/>");
        }

        return sb.Append("</worksheet>").ToString();
    }

    private static string Column(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }

    // ───────────── drawing: shapes, lines, pictures ─────────────

    private static string? Drawing(SheetLayout layout, IReadOnlyDictionary<Guid, byte[]> images, List<byte[]> media, List<(string Rel, int Media)> pictures)
    {
        var sb = new StringBuilder();
        var id = 1;
        foreach (var (e, x, y) in layout.Placed.OrderBy(p => p.Element.Z))
        {
            switch (e.Type)
            {
                case "chart" or "image":
                    if (images.TryGetValue(e.Id, out var bytes))
                    {
                        media.Add(bytes);
                        var rel = $"rId{pictures.Count + 1}";
                        pictures.Add((rel, media.Count));
                        sb.Append(Anchor(x, y, e.W, e.H, Picture(++id, e, rel)));
                    }

                    break;
                case "line" or "arrow":
                    sb.Append(Anchor(x, y, e.W, e.H, Connector(++id, e)));
                    break;
                default:
                    sb.Append(Anchor(x, y, e.W, e.H, Shape(++id, e)));
                    break;
            }
        }

        return sb.Length == 0
            ? null
            : "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" + sb + "</xdr:wsDr>";
    }

    private static string DrawingRels(List<(string Rel, int Media)> pictures)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        foreach (var (rel, media) in pictures)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"{rel}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/image{media}.png\"/>");
        }

        return sb.Append("</Relationships>").ToString();
    }

    private static string Anchor(double x, double y, double w, double h, string body) =>
        $"<xdr:absoluteAnchor><xdr:pos x=\"{Px(x)}\" y=\"{Px(y)}\"/><xdr:ext cx=\"{Px(Math.Max(1, w))}\" cy=\"{Px(Math.Max(1, h))}\"/>{body}<xdr:clientData/></xdr:absoluteAnchor>";

    private static long Px(double value) => (long)Math.Round(value * Emu);

    private static string Picture(int id, PlaygroundElementDto e, string rel) =>
        $"<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"{id}\" name=\"{Escape(e.Type)} {id}\"/><xdr:cNvPicPr><a:picLocks noChangeAspect=\"0\"/></xdr:cNvPicPr></xdr:nvPicPr>" +
        $"<xdr:blipFill><a:blip r:embed=\"{rel}\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>" +
        $"<xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Px(e.W)}\" cy=\"{Px(e.H)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic>";

    private static string Connector(int id, PlaygroundElementDto e)
    {
        var s = e.Style ?? new PlaygroundStyleDto();
        var colour = Hex(s.Stroke) ?? "2F528F";
        var width = Px(Math.Max(0.5, s.StrokeWidth));
        var flip = e.Flip ? " flipV=\"1\"" : string.Empty;
        var tail = e.Type == "arrow" ? "<a:tailEnd type=\"triangle\"/>" : string.Empty;
        return $"<xdr:cxnSp macro=\"\"><xdr:nvCxnSpPr><xdr:cNvPr id=\"{id}\" name=\"{e.Type} {id}\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr>" +
               $"<xdr:spPr><a:xfrm{flip}><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Px(e.W)}\" cy=\"{Px(e.H)}\"/></a:xfrm><a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom>" +
               $"<a:ln w=\"{width}\" cap=\"rnd\"><a:solidFill><a:srgbClr val=\"{colour}\"/></a:solidFill>{tail}</a:ln></xdr:spPr></xdr:cxnSp>";
    }

    private static string Shape(int id, PlaygroundElementDto e)
    {
        var s = e.Style ?? new PlaygroundStyleDto();
        var geometry = e.Type switch
        {
            "ellipse" => "ellipse",
            "triangle" => "triangle",
            "diamond" => "diamond",
            "hexagon" => "hexagon",
            "star" => "star5",
            "rounded" => "roundRect",
            _ => s.Radius > 0 ? "roundRect" : "rect",
        };
        var adjust = geometry == "roundRect"
            ? $"<a:avLst><a:gd name=\"adj\" fmla=\"val {(int)Math.Clamp(s.Radius <= 0 ? 16667 : s.Radius / Math.Max(1, Math.Min(e.W, e.H)) * 100000, 0, 50000)}\"/></a:avLst>"
            : "<a:avLst/>";
        var fill = Hex(s.Fill) is { } f
            ? $"<a:solidFill><a:srgbClr val=\"{f}\">{(s.Opacity < 1 ? $"<a:alpha val=\"{(int)(s.Opacity * 100000)}\"/>" : string.Empty)}</a:srgbClr></a:solidFill>"
            : "<a:noFill/>";
        var line = Hex(s.Stroke) is { } stroke && s.StrokeWidth > 0
            ? $"<a:ln w=\"{Px(s.StrokeWidth)}\"><a:solidFill><a:srgbClr val=\"{stroke}\"/></a:solidFill></a:ln>"
            : "<a:ln><a:noFill/></a:ln>";
        var anchor = s.VAlign switch { "top" => "t", "bottom" => "b", _ => "ctr" };
        return $"<xdr:sp macro=\"\" textlink=\"\"><xdr:nvSpPr><xdr:cNvPr id=\"{id}\" name=\"{Escape(e.Type)} {id}\"/><xdr:cNvSpPr/></xdr:nvSpPr>" +
               $"<xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Px(e.W)}\" cy=\"{Px(e.H)}\"/></a:xfrm><a:prstGeom prst=\"{geometry}\">{adjust}</a:prstGeom>{fill}{line}</xdr:spPr>" +
               $"<xdr:txBody><a:bodyPr vertOverflow=\"clip\" horzOverflow=\"clip\" wrap=\"square\" lIns=\"72000\" tIns=\"72000\" rIns=\"72000\" bIns=\"72000\" rtlCol=\"0\" anchor=\"{anchor}\"/><a:lstStyle/>{Paragraphs(e, s)}</xdr:txBody></xdr:sp>";
    }

    private static string Paragraphs(PlaygroundElementDto e, PlaygroundStyleDto s)
    {
        var align = s.Align switch { "center" => "ctr", "right" => "r", _ => "l" };
        var colour = Hex(s.Color) ?? (e.Type == "link" ? "1D4ED8" : "0F172A");
        var lines = (e.Text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            // A KPI card shows its label small above the value.
            var size = e.Type == "kpi" && i == 0 && lines.Length > 1 ? s.FontSize * 0.45 : s.FontSize;
            var bold = s.Bold || e.Type == "title";
            var underline = s.Underline || e.Type == "link";
            sb.Append(CultureInfo.InvariantCulture, $"<a:p><a:pPr algn=\"{align}\"/>");
            if (lines[i].Length > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<a:r><a:rPr lang=\"en-US\" sz=\"{(int)Math.Clamp(size * 75, 100, 40000)}\" b=\"{(bold ? 1 : 0)}\" i=\"{(s.Italic ? 1 : 0)}\" u=\"{(underline ? "sng" : "none")}\"><a:solidFill><a:srgbClr val=\"{colour}\"/></a:solidFill></a:rPr><a:t>{Escape(lines[i])}</a:t></a:r>");
            }
            else
            {
                sb.Append("<a:endParaRPr lang=\"en-US\"/>");
            }

            sb.Append("</a:p>");
        }

        return sb.ToString();
    }

    /// <summary>"#rgb", "#rrggbb" or "#rrggbbaa" to the six hex digits DrawingML wants; anything else (transparent, empty) is no colour.</summary>
    private static string? Hex(string? colour)
    {
        if (string.IsNullOrEmpty(colour) || colour[0] != '#')
        {
            return null;
        }

        var h = colour[1..];
        return h.Length switch
        {
            3 => string.Concat(h.Select(c => $"{c}{c}")).ToUpperInvariant(),
            6 or 8 => h[..6].ToUpperInvariant(),
            _ => null,
        };
    }

    // ───────────── package parts ─────────────

    /// <summary>Escapes XML and drops the control characters XML forbids (they would make Excel report a damaged file).</summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is < ' ' and not ('\t' or '\n' or '\r') or '\uFFFE' or '\uFFFF')
            {
                continue;
            }

            sb.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => ch.ToString() });
        }

        return sb.ToString();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(new UTF8Encoding(false).GetBytes(content));
    }

    private static string ContentTypes(int count, List<int> drawings)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var i = 1; i <= count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        foreach (var d in drawings)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/xl/drawings/drawing{d}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
        }

        return sb.Append("</Types>").ToString();
    }

    private static string Workbook(List<string> names, int count)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (var i = 0; i < count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<sheet name=\"{Escape(names[i])}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        }

        return sb.Append("</sheets></workbook>").ToString();
    }

    private static string WorkbookRels(int count)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 1; i <= count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        return sb.Append("</Relationships>").ToString();
    }

    private const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font></fonts>" +
        "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF4472C4\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/><xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/></cellXfs>" +
        "</styleSheet>";
}
