using InsightFlow.Domain.Workspace;

namespace InsightFlow.Domain.Tests.Workspace;

public sealed class ItemNameTests
{
    [Theory]
    [InlineData("sales.csv")]
    [InlineData("Q3 report (final).xlsx")]
    [InlineData(".env")]
    [InlineData("Ünïcödé — 数据")]
    [InlineData("a")]
    public void TryCreate_ValidName_Succeeds(string value)
    {
        ItemName.TryCreate(value, out var name, out var error).ShouldBeTrue(error);
        name.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:")]
    [InlineData("what?")]
    [InlineData("star*")]
    [InlineData("pipe|")]
    [InlineData("quote\"")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("com1")]
    [InlineData("tab\there")]
    public void TryCreate_InvalidName_FailsWithReason(string value)
    {
        ItemName.TryCreate(value, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryCreate_TooLong_Fails()
    {
        ItemName.TryCreate(new string('a', ItemName.MaxLength + 1), out _, out _).ShouldBeFalse();
        ItemName.TryCreate(new string('a', ItemName.MaxLength), out _, out _).ShouldBeTrue();
    }

    [Fact]
    public void Create_TrimsWhitespace()
    {
        ItemName.Create("  sales.csv  ").Value.ShouldBe("sales.csv");
    }

    [Fact]
    public void Create_Invalid_ThrowsDomainRuleException()
    {
        Should.Throw<DomainRuleException>(() => ItemName.Create("..")).Code.ShouldBe("invalid_name");
    }

    [Theory]
    [InlineData("sales.CSV", ".csv", "sales")]
    [InlineData("archive.tar.gz", ".gz", "archive.tar")]
    [InlineData(".env", "", ".env")]
    [InlineData("README", "", "README")]
    public void ExtensionAndStem_AreParsed(string value, string extension, string stem)
    {
        var name = ItemName.Create(value);

        name.Extension.ShouldBe(extension);
        name.Stem.ShouldBe(stem);
    }

    [Fact]
    public void Collides_IsCaseInsensitive()
    {
        ItemName.Create("Sales.csv").Collides(ItemName.Create("SALES.CSV")).ShouldBeTrue();
        ItemName.Create("Sales.csv").Collides(ItemName.Create("Sales.tsv")).ShouldBeFalse();
    }

    [Fact]
    public void NextAvailable_AddsCounterBeforeExtension()
    {
        var existing = new[] { "sales.csv", "Sales (2).csv", "other.csv" }.Select(ItemName.Create);

        NameConflicts.NextAvailable(ItemName.Create("SALES.csv"), existing).Value.ShouldBe("SALES (3).csv");
    }

    [Fact]
    public void NextAvailable_NoConflict_ReturnsDesired()
    {
        NameConflicts.NextAvailable(ItemName.Create("Q3"), [ItemName.Create("Q4")]).Value.ShouldBe("Q3");
    }

    [Fact]
    public void WithCounter_LongName_StaysWithinMaxLength()
    {
        var name = ItemName.Create(new string('x', ItemName.MaxLength - 4) + ".csv");

        var renamed = name.WithCounter(12);

        renamed.Value.Length.ShouldBe(ItemName.MaxLength);
        renamed.Value.ShouldEndWith(" (12).csv");
    }

    [Fact]
    public void UploadPath_Nested_SplitsFoldersAndFile()
    {
        var path = UploadPath.Parse("Q3\\eu/sales.csv");

        path.Folders.Select(f => f.Value).ShouldBe(["Q3", "eu"]);
        path.FileName.Value.ShouldBe("sales.csv");
        path.ToString().ShouldBe("Q3/eu/sales.csv");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../b.csv")]
    [InlineData("a//b.csv")]
    [InlineData("C:/data.csv")]
    public void UploadPath_Unsafe_IsRejected(string value)
    {
        Should.Throw<DomainRuleException>(() => UploadPath.Parse(value));
    }

    [Fact]
    public void UploadPath_TooDeep_IsRejected()
    {
        var deep = string.Join('/', Enumerable.Repeat("d", FolderTreeRules.MaxDepth)) + "/f.csv";

        Should.Throw<DomainRuleException>(() => UploadPath.Parse(deep)).Code.ShouldBe("upload_path_too_deep");
    }
}
