namespace InsightFlow.Tests.Infrastructure.Connectors;

public sealed class SchemaAssemblyTests
{
    private static (string Schema, string Name, string Kind, SourceColumn Column) Col(string table, string column, string type = "int", bool nullable = true) =>
        ("dbo", table, "table", new SourceColumn(column, type, nullable, false));

    [Fact]
    public void Assemble_MarksPrimaryKeys_AndGroupsColumnsPerTable()
    {
        var schema = SchemaAssembly.Assemble(
            [Col("Orders", "id"), Col("Orders", "customer_id"), Col("Customers", "id")],
            [("dbo", "Orders", "id"), ("dbo", "Customers", "id")],
            [],
            null);

        schema.Tables.Select(t => t.Table.Id).ShouldBe(["dbo.Customers", "dbo.Orders"]);
        schema.Tables.Single(t => t.Table.Name == "Orders").Columns.Single(c => c.IsPrimaryKey).Name.ShouldBe("id");
    }

    [Fact]
    public void Assemble_RequestedTablesOnly_AndForeignKeysLeavingThem()
    {
        var fks = new[]
        {
            new SourceForeignKey("dbo.Orders", "customer_id", "dbo.Customers", "id"),
            new SourceForeignKey("dbo.Orders", "region_id", "dbo.Regions", "id"), // Regions not requested
        };

        var schema = SchemaAssembly.Assemble(
            [Col("Orders", "customer_id"), Col("Customers", "id"), Col("Regions", "id")], [], fks, ["dbo.orders", "dbo.CUSTOMERS"]);

        schema.Tables.Count.ShouldBe(2);
        schema.ForeignKeys.ShouldBe(fks); // keeps every key that leaves a requested table, so a preview can show where it points
    }

    [Fact]
    public void Assemble_PrimaryKeyAlreadyOnTheColumn_IsKept()
    {
        var column = ("dbo", "T", "table", new SourceColumn("id", "int", false, true)); // e.g. MySQL COLUMN_KEY = PRI

        SchemaAssembly.Assemble([column], [], [], null).Tables.Single().Columns.Single().IsPrimaryKey.ShouldBeTrue();
    }

    [Theory]
    [InlineData("nvarchar", 100, "nvarchar(100)")]
    [InlineData("nvarchar", -1, "nvarchar")]
    [InlineData("int", null, "int")]
    public void TypeWithLength_AddsTheLengthOnlyWhenThereIsOne(string type, int? length, string expected) =>
        SchemaAssembly.TypeWithLength(type, length).ShouldBe(expected);
}
