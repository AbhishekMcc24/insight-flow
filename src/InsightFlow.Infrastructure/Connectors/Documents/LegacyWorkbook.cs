using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;
using ExcelDataReader;

namespace InsightFlow.Infrastructure.Connectors;

/// <summary>
/// Old Excel workbooks (.xls) are not read by DuckDB, so they are first rewritten as .xlsx, cell by cell with the same types (numbers stay
/// numbers, text stays text, dates stay dates), and DuckDB then parses that copy exactly like any other workbook. Problems with mixed types
/// therefore show up the same way. The person's file is never changed; the copy lives in the work directory for the length of one run.
/// </summary>
public static class LegacyWorkbook
{
    static LegacyWorkbook() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Writes <paramref name="targetXlsx"/> from the workbook at <paramref name="sourcePath"/>. Throws <see cref="SpreadsheetException"/> when it cannot be read.</summary>
    public static void ConvertToXlsx(string sourcePath, string targetXlsx)
    {
        var sheets = new List<(string Name, string Xml)>();
        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            do
            {
                var name = Unique(string.IsNullOrWhiteSpace(reader.Name) ? $"Sheet{sheets.Count + 1}" : reader.Name, used);
                sheets.Add((name, SheetXml(reader)));
            }
            while (reader.NextResult());
        }
        catch (Exception ex) when (ex is ExcelDataReader.Exceptions.ExcelReaderException or InvalidDataException or IOException or NotSupportedException)
        {
            throw new SpreadsheetException($"The old .xls workbook could not be read: {ex.Message.Split('\n', 2)[0].Trim()}");
        }

        Write(targetXlsx, sheets);
    }

    private static string SheetXml(IExcelDataReader reader)
    {
        var sb = new StringBuilder();
        var row = 0;
        while (reader.Read())
        {
            row++;
            var cells = new StringBuilder();
            for (var c = 0; c < reader.FieldCount; c++)
            {
                var reference = Column(c) + row.ToString(CultureInfo.InvariantCulture);
                switch (reader.GetValue(c))
                {
                    case null:
                        break;
                    case string text:
                        cells.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Clean(text)}</t></is></c>");
                        break;
                    case bool flag:
                        cells.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"b\"><v>{(flag ? 1 : 0)}</v></c>");
                        break;
                    case DateTime date:
                        cells.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" s=\"1\"><v>{date.ToOADate().ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    case IConvertible number when number is not char:
                        cells.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"><v>{Convert.ToDouble(number, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    default:
                        cells.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{Clean(Convert.ToString(reader.GetValue(c), CultureInfo.InvariantCulture) ?? string.Empty)}</t></is></c>");
                        break;
                }
            }

            if (cells.Length > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{row}\">").Append(cells).Append("</row>");
            }
        }

        // The dimension tells readers where the sheet ends without scanning it.
        var dimension = $"A1:{Column(Math.Max(0, reader.FieldCount - 1))}{Math.Max(1, row)}";
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><dimension ref="{dimension}"/><sheetData>{sb}</sheetData></worksheet>""";
    }

    private static void Write(string target, List<(string Name, string Xml)> sheets)
    {
        using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>"""
            + string.Concat(sheets.Select((_, i) => $"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"""))
            + "</Types>");
        Add(zip, "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Add(zip, "xl/workbook.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>"""
            + string.Concat(sheets.Select((s, i) => $"""<sheet name="{SecurityElement.Escape(s.Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>"""))
            + "</sheets></workbook>");
        Add(zip, "xl/_rels/workbook.xml.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">"""
            + string.Concat(sheets.Select((_, i) => $"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>"""))
            + """<Relationship Id="rIdStyles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");

        // Style 1 is a date (built-in number format 14), so DuckDB reads date cells as dates.
        Add(zip, "xl/styles.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs></styleSheet>""");
        for (var i = 0; i < sheets.Count; i++)
        {
            Add(zip, $"xl/worksheets/sheet{i + 1}.xml", sheets[i].Xml);
        }
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>XML-safe text: control characters Excel files can carry are dropped.</summary>
    private static string Clean(string text) => SecurityElement.Escape(new string([.. text.Where(XmlConvert.IsXmlChar)])) ?? string.Empty;

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name.Length > 31 ? name[..31] : name;
        for (var i = 2; !used.Add(candidate); i++)
        {
            var suffix = $" ({i})";
            candidate = (name.Length > 31 - suffix.Length ? name[..(31 - suffix.Length)] : name) + suffix;
        }

        return candidate;
    }

    private static string Column(int index)
    {
        var name = string.Empty;
        for (var n = index; n >= 0; n = n / 26 - 1)
        {
            name = (char)('A' + n % 26) + name;
        }

        return name;
    }
}
