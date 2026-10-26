using System.IO.Compression;
using System.Security;
using System.Text;

namespace InsightFlow.Connectors.Tests;

/// <summary>Builds small .xlsx workbooks for tests, with exact control over cell types (a number in one row, text in the next).</summary>
internal static class XlsxBuilder
{
    /// <summary>Each sheet is a name and its rows; a cell is a string, a number, a bool or null (empty).</summary>
    public static byte[] Build(params (string Name, IReadOnlyList<object?[]> Rows)[] sheets)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>"""
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
                + "</Relationships>");
            for (var i = 0; i < sheets.Length; i++)
            {
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i].Rows));
            }
        }

        return memory.ToArray();
    }

    public static string Write(string directory, string name, params (string Name, IReadOnlyList<object?[]> Rows)[] sheets)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Build(sheets));
        return path;
    }

    private static string SheetXml(IReadOnlyList<object?[]> rows)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var reference = $"{Column(c)}{r + 1}";
                switch (rows[r][c])
                {
                    case null:
                        break;
                    case string text:
                        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(text)}</t></is></c>");
                        break;
                    case bool flag:
                        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"b\"><v>{(flag ? 1 : 0)}</v></c>");
                        break;
                    default:
                        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"<c r=\"{reference}\"><v>{Convert.ToString(rows[r][c], System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                        break;
                }
            }

            sb.Append("</row>");
        }

        return sb.Append("</sheetData></worksheet>").ToString();
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

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}

