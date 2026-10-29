namespace InsightFlow.Tests.Infrastructure.Connectors;

public sealed class LegacyWorkbookTests
{
    [Fact]
    public void ConvertToXlsx_NotAWorkbook_ThrowsSpreadsheetException()
    {
        var source = Path.Combine(Path.GetTempPath(), $"lw-{Guid.NewGuid():N}.xls");
        var target = Path.Combine(Path.GetTempPath(), $"lw-{Guid.NewGuid():N}.xlsx");
        File.WriteAllText(source, "this is not a workbook");
        try
        {
            Should.Throw<SpreadsheetException>(() => LegacyWorkbook.ConvertToXlsx(source, target));
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
        }
    }
}
