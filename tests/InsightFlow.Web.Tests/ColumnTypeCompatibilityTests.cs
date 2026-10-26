using InsightFlow.Contracts.Knowledge;

namespace InsightFlow.Web.Tests;

public sealed class ColumnTypeCompatibilityTests
{
    [Theory]
    [InlineData("int", "bigint")]
    [InlineData("bigint", "decimal(18,2)")]
    [InlineData("varchar(50)", "nvarchar(200)")]
    [InlineData("character varying", "text")]
    [InlineData("datetime", "date")]
    [InlineData("uniqueidentifier", "uuid")]
    [InlineData("INT UNSIGNED", "smallint")]
    [InlineData("unknown", "bigint")]
    public void AreCompatible_SameFamily_IsTrue(string a, string b) => ColumnTypeCompatibility.AreCompatible(a, b).ShouldBeTrue();

    [Theory]
    [InlineData("varchar(50)", "bigint")]
    [InlineData("datetime", "decimal")]
    [InlineData("char(1)", "int")]
    [InlineData("bit", "varchar")]
    [InlineData("uuid", "int")]
    [InlineData("geometry", "int")]
    public void AreCompatible_DifferentKinds_IsFalse(string a, string b) => ColumnTypeCompatibility.AreCompatible(a, b).ShouldBeFalse();

    [Fact]
    public void AreCompatible_SameUnrecognisedType_IsTrue() => ColumnTypeCompatibility.AreCompatible("geometry", "GEOMETRY").ShouldBeTrue();
}
