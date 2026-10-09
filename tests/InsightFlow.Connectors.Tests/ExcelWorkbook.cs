using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace InsightFlow.Connectors.Tests;

/// <summary>Writes a small .xlsx with the BCL zip APIs so connector tests need no Excel library.</summary>
internal static class ExcelWorkbook
{
    public static void WriteOrders(string path, int rows)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", ContentTypes);
        Add(zip, "_rels/.rels", RootRels);
        Add(zip, "xl/workbook.xml", Workbook);
        Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
        Add(zip, "xl/styles.xml", Styles);
        Add(zip, "xl/worksheets/sheet1.xml", OrdersSheet(rows));
        Add(zip, "xl/worksheets/sheet2.xml", NotesSheet);
    }

    private static void Add(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(xml);
    }

    private static string OrdersSheet(int rows)
    {
        var body = new StringBuilder();
        body.Append("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
            <row r="1"><c r="A1" t="inlineStr"><is><t>id</t></is></c><c r="B1" t="inlineStr"><is><t>region</t></is></c><c r="C1" t="inlineStr"><is><t>amount</t></is></c><c r="D1" t="inlineStr"><is><t>ordered_on</t></is></c></row>
            """);
        string[] regions = ["North", "South", "East"];
        for (var i = 1; i <= rows; i++)
        {
            var excelRow = i + 1;
            var region = i % 4 == 0 ? null : regions[i % 4 - 1];
            var serial = new DateTime(2025, 1, 1).AddDays(i).ToOADate().ToString(CultureInfo.InvariantCulture);
            var amount = (i * 1.25m).ToString(CultureInfo.InvariantCulture);
            body.Append(CultureInfo.InvariantCulture, $"<row r=\"{excelRow}\"><c r=\"A{excelRow}\"><v>{i}</v></c>");
            if (region is not null)
            {
                body.Append(CultureInfo.InvariantCulture, $"<c r=\"B{excelRow}\" t=\"inlineStr\"><is><t>{region}</t></is></c>");
            }

            body.Append(CultureInfo.InvariantCulture, $"<c r=\"C{excelRow}\"><v>{amount}</v></c><c r=\"D{excelRow}\" s=\"1\"><v>{serial}</v></c></row>");
        }

        body.Append("</sheetData></worksheet>");
        return body.ToString();
    }

    private const string ContentTypes =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
        </Types>
        """;

    private const string RootRels =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private const string Workbook =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets>
            <sheet name="Orders" sheetId="1" r:id="rId1"/>
            <sheet name="Notes" sheetId="2" r:id="rId2"/>
          </sheets>
        </workbook>
        """;

    private const string WorkbookRels =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
          <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """;

    private const string Styles =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
          <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="2">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
          </cellXfs>
        </styleSheet>
        """;

    private const string NotesSheet =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
          <row r="1"><c r="A1" t="inlineStr"><is><t>note</t></is></c></row>
          <row r="2"><c r="A2" t="inlineStr"><is><t>hello</t></is></c></row>
        </sheetData></worksheet>
        """;
}
