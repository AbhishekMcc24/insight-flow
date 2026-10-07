using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Validation;
using InsightFlow.Domain.Viz;
using InsightFlow.Testing;
using SortDirection = InsightFlow.Domain.Viz.SortDirection;

namespace InsightFlow.Domain.Tests.Validation;

public sealed class VizSpecValidatorTests
{
    private static readonly SemanticModel Model = RetailModel.Create();

    private static VizSpec Bar(
        FieldRef? x = null,
        FieldRef? y = null,
        IReadOnlyList<FilterSpec>? filters = null,
        IReadOnlyList<SortSpec>? sort = null,
        int limit = VizSpec.DefaultLimit,
        Mark mark = Mark.Bar,
        FieldRef? color = null) =>
        new(
            VizSpec.CurrentSchemaVersion,
            RetailModel.SalesVersionId,
            mark,
            new Encoding(x ?? new FieldRef("channel"), y ?? new FieldRef("revenue", Agg.Sum), color),
            filters ?? [],
            sort,
            limit);

    private static IEnumerable<string> Codes(ValidationResult result) => result.Errors.Select(e => e.Code);

    [Fact]
    public void Validate_ValidBarChart_IsValid()
    {
        var spec = Bar(
            x: new FieldRef("order_date", TimeUnit: TimeUnit.Month),
            filters: [new InFilter("stores.region", ["North", "West"]), new RangeFilter("revenue", Min: 0)],
            sort: [new SortSpec("order_date", SortDirection.Asc)]);

        var result = VizSpecValidator.Validate(spec, Model);

        result.IsValid.ShouldBeTrue(result.ToString());
    }

    [Fact]
    public void Validate_AllFixtures_AreValid()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.json"))
        {
            var spec = VizSpecJson.Deserialize(File.ReadAllText(file));

            var result = VizSpecValidator.Validate(spec, Model);

            result.IsValid.ShouldBeTrue($"{Path.GetFileName(file)}: {result}");
        }
    }

    [Theory]
    [InlineData("does_not_exist", "unknown_field")]
    [InlineData("products.nope", "unknown_field")]
    [InlineData("product_id", "unknown_field")] // ambiguous: sales.product_id vs products.product_id
    public void Validate_BadFieldOnX_ReportsUnknownField(string field, string code)
    {
        var result = VizSpecValidator.Validate(Bar(x: new FieldRef(field)), Model);

        Codes(result).ShouldContain(code);
        result.Errors.ShouldContain(e => e.Path == "encoding.x");
    }

    [Fact]
    public void Validate_AmbiguousField_ExplainsHowToQualify()
    {
        var result = VizSpecValidator.Validate(Bar(x: new FieldRef("store_id")), Model);

        result.Errors.Single().Message.ShouldContain("sales.store_id");
        result.Errors.Single().Message.ShouldContain("stores.store_id");
    }

    [Theory]
    [InlineData("channel", Agg.Sum)]
    [InlineData("channel", Agg.Avg)]
    [InlineData("order_date", Agg.Median)]
    [InlineData("is_returned", Agg.Max)]
    public void Validate_IllegalAggregation_IsRejected(string field, Agg agg)
    {
        var result = VizSpecValidator.Validate(Bar(y: new FieldRef(field, agg)), Model);

        Codes(result).ShouldContain("illegal_aggregation");
    }

    [Theory]
    [InlineData("channel", Agg.CountDistinct)]
    [InlineData("order_date", Agg.Max)]
    [InlineData("quantity", Agg.Median)]
    [InlineData("is_returned", Agg.Count)]
    public void Validate_LegalAggregation_IsAccepted(string field, Agg agg)
    {
        VizSpecValidator.Validate(Bar(y: new FieldRef(field, agg)), Model).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_TimeUnitOnNonDate_IsRejected()
    {
        var result = VizSpecValidator.Validate(Bar(x: new FieldRef("channel", TimeUnit: TimeUnit.Month)), Model);

        Codes(result).ShouldContain("time_unit_not_allowed");
    }

    [Fact]
    public void Validate_HourOnDateColumn_IsRejected_ButAllowedOnDateTime()
    {
        Codes(VizSpecValidator.Validate(Bar(x: new FieldRef("order_date", TimeUnit: TimeUnit.Hour)), Model))
            .ShouldContain("time_unit_not_allowed");
        VizSpecValidator.Validate(Bar(x: new FieldRef("ordered_at", TimeUnit: TimeUnit.Hour)), Model)
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_MeasureWithAggregation_IsRejected()
    {
        var result = VizSpecValidator.Validate(Bar(y: new FieldRef("profit", Agg.Sum)), Model);

        Codes(result).ShouldContain("measure_already_aggregated");
    }

    [Fact]
    public void Validate_CalculatedMeasureOnY_IsValid()
    {
        VizSpecValidator.Validate(Bar(y: new FieldRef("profit")), Model).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(50_001)]
    public void Validate_LimitOutOfRange_IsRejected(int limit)
    {
        Codes(VizSpecValidator.Validate(Bar(limit: limit), Model)).ShouldContain("limit_out_of_range");
    }

    [Fact]
    public void Validate_LimitAtMaximum_IsValid()
    {
        VizSpecValidator.Validate(Bar(limit: VizSpec.MaxLimit), Model).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_BarWithoutY_ReportsMissingChannel()
    {
        var spec = Bar() with { Encoding = new Encoding(new FieldRef("channel"), null) };

        var result = VizSpecValidator.Validate(spec, Model);

        result.Errors.ShouldContain(e => e.Code == "missing_channel" && e.Path == "encoding.y");
    }

    [Fact]
    public void Validate_PieWithX_IsRejected()
    {
        var spec = Bar(mark: Mark.Pie, color: new FieldRef("category"));

        Codes(VizSpecValidator.Validate(spec, Model)).ShouldContain("channel_not_allowed");
    }

    [Fact]
    public void Validate_HeatmapWithoutColor_ReportsMissingChannel()
    {
        Codes(VizSpecValidator.Validate(Bar(mark: Mark.Heatmap), Model)).ShouldContain("missing_channel");
    }

    [Fact]
    public void Validate_SortOnUnencodedField_IsRejected()
    {
        var result = VizSpecValidator.Validate(Bar(sort: [new SortSpec("region", SortDirection.Desc)]), Model);

        Codes(result).ShouldContain("sort_field_not_encoded");
    }

    [Fact]
    public void Validate_WrongSchemaVersionAndDataset_ReportsBoth()
    {
        var spec = Bar() with { SchemaVersion = 2, DatasetVersionId = Guid.NewGuid() };

        var result = VizSpecValidator.Validate(spec, Model);

        Codes(result).ShouldBe(["unsupported_schema_version", "dataset_not_in_model"], ignoreOrder: true);
    }

    [Fact]
    public void Validate_RangeWithoutBounds_IsRejected()
    {
        Codes(VizSpecValidator.Validate(Bar(filters: [new RangeFilter("revenue")]), Model)).ShouldContain("range_without_bounds");
    }

    [Fact]
    public void Validate_RangeOnString_IsRejected()
    {
        Codes(VizSpecValidator.Validate(Bar(filters: [new RangeFilter("channel", Min: "A")]), Model)).ShouldContain("range_on_unordered_type");
    }

    [Theory]
    [InlineData("revenue", "lots")] // string for a decimal
    [InlineData("order_date", "not-a-date")]
    [InlineData("is_returned", "yes")]
    public void Validate_LiteralTypeMismatch_IsRejected(string field, string value)
    {
        var result = VizSpecValidator.Validate(Bar(filters: [new EqualsFilter(field, value)]), Model);

        result.Errors.ShouldContain(e => e.Code == "literal_type_mismatch" && e.Path == "filters[0].value");
    }

    [Fact]
    public void Validate_NumberForStringColumn_IsRejected()
    {
        Codes(VizSpecValidator.Validate(Bar(filters: [new EqualsFilter("channel", 3)]), Model)).ShouldContain("literal_type_mismatch");
    }

    [Fact]
    public void Validate_EmptyInFilter_IsRejected()
    {
        Codes(VizSpecValidator.Validate(Bar(filters: [new InFilter("channel", [])]), Model)).ShouldContain("in_values_out_of_range");
    }

    [Fact]
    public void Validate_TopNByRawField_IsRejected()
    {
        var spec = Bar(filters: [new TopNFilter("product_name", 5, new FieldRef("revenue"))]);

        Codes(VizSpecValidator.Validate(spec, Model)).ShouldContain("top_n_by_not_aggregated");
    }

    [Fact]
    public void Validate_TopNByMeasure_IsValid()
    {
        var spec = Bar(filters: [new TopNFilter("product_name", 5, new FieldRef("profit"))]);

        VizSpecValidator.Validate(spec, Model).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_RelativeDateOnString_IsRejected()
    {
        var spec = Bar(filters: [new RelativeDateFilter("channel", TimeUnit.Month, RelativeDateAnchor.Last, 3)]);

        Codes(VizSpecValidator.Validate(spec, Model)).ShouldContain("relative_date_on_non_date");
    }

    [Fact]
    public void Validate_MultipleProblems_ReportsAllAtOnce()
    {
        var spec = Bar(x: new FieldRef("nope"), y: new FieldRef("channel", Agg.Sum), limit: 0);

        VizSpecValidator.Validate(spec, Model).Errors.Count.ShouldBeGreaterThanOrEqualTo(3);
    }
}
