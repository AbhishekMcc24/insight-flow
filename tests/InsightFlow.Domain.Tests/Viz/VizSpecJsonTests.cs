using System.Text.Json;
using InsightFlow.Domain.Viz;
using InsightFlow.Testing;
using SortDirection = InsightFlow.Domain.Viz.SortDirection;

namespace InsightFlow.Domain.Tests.Viz;

public sealed class VizSpecJsonTests
{
    public static readonly TheoryData<string> Fixtures = new(
        "bar-revenue-by-month.json",
        "heatmap-all-filters.json",
        "pie-units-by-category.json");

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Deserialize_Fixture_RoundTripsToEqualSpec(string fixture)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));

        var spec = VizSpecJson.Deserialize(json);
        var roundTripped = VizSpecJson.Deserialize(VizSpecJson.Serialize(spec));

        roundTripped.ShouldBe(spec);
    }

    [Fact]
    public void Deserialize_AllFilterTypes_ProducesTypedFilters()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "heatmap-all-filters.json"));

        var spec = VizSpecJson.Deserialize(json);

        spec.Filters.Select(f => f.GetType()).ShouldBe(
        [
            typeof(EqualsFilter), typeof(EqualsFilter), typeof(InFilter), typeof(RangeFilter), typeof(RangeFilter), typeof(TopNFilter),
        ]);
        spec.Filters[1].ShouldBe(new EqualsFilter("is_returned", false, Exclude: true));
        var range = spec.Filters[3].ShouldBeOfType<RangeFilter>();
        range.Min.ShouldBe(ScalarValue.Of(10m));
        range.Max.ShouldBe(ScalarValue.Of(2500.5m));
        range.MaxInclusive.ShouldBeFalse();
        spec.Filters[5].ShouldBe(new TopNFilter("product_name", 10, new FieldRef("revenue", Agg.Sum)));
    }

    [Fact]
    public void Deserialize_DiscriminatorNotFirst_IsAccepted()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pie-units-by-category.json"));

        var spec = VizSpecJson.Deserialize(json);

        spec.Filters.ShouldHaveSingleItem().ShouldBeOfType<InFilter>().Exclude.ShouldBeTrue();
        spec.Limit.ShouldBe(VizSpec.DefaultLimit);
    }

    [Fact]
    public void Deserialize_UnknownFilterType_Throws()
    {
        const string json = """
            {"schemaVersion":1,"datasetVersionId":"5a1e5000-0000-4000-8000-000000000001","mark":"Bar",
             "encoding":{"x":{"field":"channel"},"y":{"field":"revenue","agg":"Sum"}},
             "filters":[{"type":"sqlInjection","field":"channel"}]}
            """;

        Should.Throw<JsonException>(() => VizSpecJson.Deserialize(json));
    }

    [Fact]
    public void Deserialize_FilterValueIsObject_Throws()
    {
        const string json = """
            {"schemaVersion":1,"datasetVersionId":"5a1e5000-0000-4000-8000-000000000001","mark":"Bar",
             "encoding":{"x":{"field":"channel"},"y":{"field":"revenue","agg":"Sum"}},
             "filters":[{"type":"equals","field":"channel","value":{"nested":true}}]}
            """;

        Should.Throw<JsonException>(() => VizSpecJson.Deserialize(json));
    }

    [Fact]
    public void Serialize_RepresentativeSpec_MatchesGolden()
    {
        var spec = new VizSpec(
            VizSpec.CurrentSchemaVersion,
            RetailModel.SalesVersionId,
            Mark.Line,
            new VizEncoding(
                X: new FieldRef("order_date", TimeUnit: TimeUnit.Quarter),
                Y: new FieldRef("revenue", Agg.Sum),
                Color: new FieldRef("stores.region")),
            [
                new InFilter("channel", ["Online", "Store"]),
                new RangeFilter("quantity", Min: 1),
                new RelativeDateFilter("order_date", TimeUnit.Year, RelativeDateAnchor.ToDate),
                new EqualsFilter("is_returned", ScalarValue.Null, Exclude: true),
            ],
            [new SortSpec("order_date", SortDirection.Asc)],
            Limit: 2_000);

        Golden.MatchJson(VizSpecJson.Serialize(spec), "line-revenue-by-quarter");
    }

    [Fact]
    public void Serialize_Enums_AreWrittenAsNames()
    {
        var spec = new VizSpec(1, Guid.Empty, Mark.Heatmap, new VizEncoding(new FieldRef("a", Agg.CountDistinct), null), []);

        var json = VizSpecJson.Serialize(spec);

        json.ShouldContain("\"mark\":\"Heatmap\"");
        json.ShouldContain("\"agg\":\"CountDistinct\"");
    }
}
