using InsightFlow.Domain.Knowledge;

namespace InsightFlow.Tests.Domain.Knowledge;

public sealed class QueryRetrievalTests
{
    private static readonly Guid Orders = Guid.NewGuid();
    private static readonly Guid Customers = Guid.NewGuid();
    private static readonly Guid Regions = Guid.NewGuid();
    private static readonly Guid Products = Guid.NewGuid();
    private static readonly Guid Island = Guid.NewGuid();

    private static JoinEdge Fk(Guid from, Guid to) => new(from, "x", to, "id", RelationshipKind.ForeignKey, 1);

    [Fact]
    public void Extract_KeepsMeaningfulWords_LowerCased_Once()
    {
        var words = QuestionTokens.Extract("What is the total Outstanding amount per customer? Show outstanding by region!");

        words.ShouldBe(["outstanding", "amount", "customer", "region"]);
    }

    [Fact]
    public void Extract_EmptyOrOnlyFillerWords_IsEmpty()
    {
        QuestionTokens.Extract("   ").ShouldBeEmpty();
        QuestionTokens.Extract("show me the top of it").ShouldBeEmpty();
        QuestionTokens.Extract(null).ShouldBeEmpty();
    }

    [Fact]
    public void Extract_NeverReturnsMoreThanTheLimit()
    {
        var question = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"word{i}"));

        QuestionTokens.Extract(question).Count.ShouldBe(QuestionTokens.MaxTokens);
    }

    [Fact]
    public void Connect_TwoTablesTwoHopsApart_AddsTheBridge()
    {
        // orders -> customers -> regions: asking about orders and regions needs customers in between.
        var result = JoinPaths.Connect([Orders, Regions], [Fk(Orders, Customers), Fk(Customers, Regions)]);

        result.Tables.ShouldBe([Orders, Customers, Regions]);
        result.Bridges.ShouldBe([Customers]);
        result.Unconnected.ShouldBeEmpty();
    }

    [Fact]
    public void Connect_PrefersForeignKeysOverSameAttributeLinks()
    {
        var sameAttribute = new JoinEdge(Orders, "region", Regions, "region", RelationshipKind.SameAttribute, 1);

        var result = JoinPaths.Connect([Orders, Regions], [sameAttribute, Fk(Orders, Customers), Fk(Customers, Regions)]);

        result.Tables.ShouldBe([Orders, Customers, Regions]); // two foreign-key hops (cost 2) beat one same-attribute hop (cost 3)
    }

    [Fact]
    public void Connect_UsesASameAttributeLinkWhenNothingElseConnects()
    {
        var result = JoinPaths.Connect([Orders, Regions], [new JoinEdge(Orders, "region", Regions, "region", RelationshipKind.SameAttribute, 1)]);

        result.Tables.ShouldBe([Orders, Regions]);
        result.Bridges.ShouldBeEmpty();
    }

    [Fact]
    public void Connect_TablesThatCannotBeReached_AreReported_NotDropped()
    {
        var result = JoinPaths.Connect([Orders, Customers, Island], [Fk(Orders, Customers)]);

        result.Tables.ShouldBe([Orders, Customers]);
        result.Unconnected.ShouldBe([Island]);
    }

    [Fact]
    public void Connect_LongerThanTheHopLimit_IsNotConnected()
    {
        var chain = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }; // 4 hops
        var edges = chain.Zip(chain.Skip(1), Fk).ToList();

        var result = JoinPaths.Connect([chain[0], chain[4]], edges);

        result.Unconnected.ShouldBe([chain[4]]);
    }

    [Fact]
    public void Connect_NoSeedsOrOneSeed_IsTrivial()
    {
        JoinPaths.Connect([], []).Tables.ShouldBeEmpty();
        JoinPaths.Connect([Products], [Fk(Orders, Customers)]).Tables.ShouldBe([Products]);
    }

    [Fact]
    public void Connect_IgnoresTheDirectionOfTheRelationship()
    {
        JoinPaths.Connect([Customers, Orders], [Fk(Orders, Customers)]).Tables.ShouldBe([Customers, Orders]);
    }
}
