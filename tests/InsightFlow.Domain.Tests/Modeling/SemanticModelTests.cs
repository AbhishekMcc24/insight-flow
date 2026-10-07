using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Validation;
using InsightFlow.Testing;

namespace InsightFlow.Domain.Tests.Modeling;

public sealed class SemanticModelTests
{
    private static readonly SemanticModel Model = RetailModel.Create();

    [Theory]
    [InlineData("revenue", "sales", "revenue")]
    [InlineData("REVENUE", "sales", "revenue")]
    [InlineData("stores.region", "stores", "region")]
    [InlineData("products.product_id", "products", "product_id")]
    public void Resolve_Column_ReturnsTableAndColumn(string field, string table, string column)
    {
        var resolution = Model.Resolve(field);

        resolution.Success.ShouldBeTrue(resolution.Error);
        resolution.Table!.Name.ShouldBe(table);
        resolution.Column!.Name.ShouldBe(column);
    }

    [Fact]
    public void Resolve_Measure_ReturnsMeasure()
    {
        var resolution = Model.Resolve("profit");

        resolution.IsMeasure.ShouldBeTrue();
        resolution.DataType.ShouldBe(DataType.Decimal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("store_id")]
    public void Resolve_UnknownOrAmbiguous_Fails(string field)
    {
        Model.Resolve(field).Success.ShouldBeFalse();
    }

    [Fact]
    public void Validate_RetailModel_IsValid()
    {
        var result = SemanticModelValidator.Validate(Model);

        result.IsValid.ShouldBeTrue(result.ToString());
    }

    [Fact]
    public void Validate_DuplicateTableAndColumn_AreReported()
    {
        var table = new ModelTable("t", Guid.NewGuid(), [new("a", DataType.String, ColumnRole.Dimension), new("A", DataType.Integer, ColumnRole.Measure)]);
        var model = Model with { Tables = [table, table with { Name = "T" }], Relationships = [], Measures = [] };

        var codes = SemanticModelValidator.Validate(model).Errors.Select(e => e.Code).ToList();

        codes.ShouldContain("duplicate_table");
        codes.ShouldContain("duplicate_column");
    }

    [Fact]
    public void Validate_RelationshipToMissingColumn_IsReported()
    {
        var model = Model with { Relationships = [new Relationship("sales", "stores", [new JoinKey("store_id", "nope")])] };

        SemanticModelValidator.Validate(model).Errors.ShouldContain(e => e.Code == "unknown_join_column");
    }

    [Fact]
    public void Validate_RelationshipWithTypeMismatch_IsReported()
    {
        var model = Model with { Relationships = [new Relationship("sales", "stores", [new JoinKey("channel", "store_id")])] };

        SemanticModelValidator.Validate(model).Errors.ShouldContain(e => e.Code == "join_type_mismatch");
    }

    [Fact]
    public void Validate_MeasureShadowingColumn_IsReported()
    {
        var model = Model with { Measures = [new CalculatedMeasure("revenue", "SUM(revenue)", DataType.Decimal)] };

        SemanticModelValidator.Validate(model).Errors.ShouldContain(e => e.Code == "measure_shadows_column");
    }
}
