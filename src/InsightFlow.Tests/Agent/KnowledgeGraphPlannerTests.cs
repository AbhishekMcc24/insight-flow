using InsightFlow.Agent.Knowledge;
using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;

namespace InsightFlow.Tests.Agent;

/// <summary>AI enrichment of the knowledge base, driven by a scripted model (no network).</summary>
public sealed class KnowledgeGraphPlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KnowledgeGraphPlan Plan(string? tableDescription = null, string? columnDescription = null) => new(
        [
            new GraphTable(Guid.NewGuid(), "dbo.orders", 0, [new GraphColumn(Guid.NewGuid(), "order_id", "int", null, true), new GraphColumn(Guid.NewGuid(), "customer_id", "int", columnDescription)], tableDescription),
            new GraphTable(Guid.NewGuid(), "dbo.customers", 0, [new GraphColumn(Guid.NewGuid(), "customer_id", "int", null, true), new GraphColumn(Guid.NewGuid(), "name", "varchar(100)")]),
        ],
        [],
        []);

    private static KnowledgeGraphPlanner Planner(ScriptedChatClient model) =>
        new(new FixedRouter(model), NullLogger<KnowledgeGraphPlanner>.Instance);

    [Fact]
    public async Task SuggestAsync_ValidAnswer_ReturnsDescriptionsAndRelationships()
    {
        var model = new ScriptedChatClient().ThenText("""
            ```json
            {"tableDescriptions":[{"table":"dbo.customers","description":"People who buy from us"}],
             "columnDescriptions":[{"table":"dbo.customers","column":"name","description":"Full name of the customer"}],
             "relationships":[{"fromTable":"dbo.orders","fromColumn":"customer_id","toTable":"dbo.customers","toColumn":"customer_id",
               "kind":"ForeignKey","confidence":0.95,"reason":"Same key name and type"}]}
            ```
            """);

        var result = await Planner(model).SuggestAsync(TenantId.New(), Plan(), onlyMissing: true, Ct);

        result.TableDescriptions.Single().Description.ShouldBe("People who buy from us");
        result.ColumnDescriptions.Single().Column.ShouldBe("name");
        result.Relationships.Single().Kind.ShouldBe(RelationshipKind.ForeignKey);
        result.DroppedRelationships.ShouldBe(0);
    }

    [Fact]
    public async Task SuggestAsync_InventedColumns_AreDroppedNotReturned()
    {
        var model = new ScriptedChatClient().ThenText("""
            {"relationships":[
              {"fromTable":"dbo.orders","fromColumn":"customer_id","toTable":"dbo.customers","toColumn":"customer_id","kind":"ForeignKey","confidence":0.9,"reason":"ok"},
              {"fromTable":"dbo.orders","fromColumn":"nope","toTable":"dbo.customers","toColumn":"customer_id","kind":"ForeignKey","confidence":0.9,"reason":"made up"},
              {"fromTable":"payments","fromColumn":"id","toTable":"dbo.customers","toColumn":"customer_id","kind":"ForeignKey","confidence":0.9,"reason":"no such table"}]}
            """);

        var result = await Planner(model).SuggestAsync(TenantId.New(), Plan(), true, Ct);

        result.Relationships.Count.ShouldBe(1);
        result.DroppedRelationships.ShouldBe(2);
    }

    [Fact]
    public async Task SuggestAsync_RelationshipAlreadyKnown_IsNotSuggestedAgain()
    {
        var plan = Plan() with { Relationships = [new GraphRelationship("dbo.customers", "customer_id", "dbo.orders", "customer_id", RelationshipKind.ForeignKey, 1, "declared")] };
        var model = new ScriptedChatClient().ThenText("""
            {"relationships":[{"fromTable":"dbo.orders","fromColumn":"customer_id","toTable":"dbo.customers","toColumn":"customer_id","kind":"ForeignKey","confidence":0.9,"reason":"same"}]}
            """);

        var result = await Planner(model).SuggestAsync(TenantId.New(), plan, true, Ct);

        result.Relationships.ShouldBeEmpty();
    }

    [Fact]
    public async Task SuggestAsync_FirstReplyNotJson_RepairsOnce()
    {
        var model = new ScriptedChatClient()
            .ThenText("Sure! Here is what I found.")
            .ThenText("""{"relationships":[]}""");

        var result = await Planner(model).SuggestAsync(TenantId.New(), Plan(), true, Ct);

        result.Relationships.ShouldBeEmpty();
        model.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SuggestAsync_NeverJson_Throws()
    {
        var model = new ScriptedChatClient().ThenText("no").ThenText("still no");

        await Should.ThrowAsync<KnowledgeGraphPlanningException>(() => Planner(model).SuggestAsync(TenantId.New(), Plan(), true, Ct));
    }

    [Fact]
    public async Task SuggestAsync_PromptCarriesSchemaOnly_AndMarksWhatNeedsDescription()
    {
        var model = new ScriptedChatClient().ThenText("""{"relationships":[]}""");

        await Planner(model).SuggestAsync(TenantId.New(), Plan(tableDescription: "Customer orders", columnDescription: "Who bought"), onlyMissing: true, Ct);

        var prompt = string.Join("\n", model.Requests[0].Select(m => m.Text));
        prompt.ShouldContain("\"dbo.orders\" — Customer orders");   // described: shown, not requested
        prompt.ShouldContain("\"dbo.customers\" (needs description)");
        prompt.ShouldContain("\"order_id\" int [primary key] (needs description)");
        prompt.ShouldContain("\"customer_id\" int — Who bought");
    }

    [Fact]
    public async Task SuggestAsync_NoTables_SkipsTheModel()
    {
        var model = new ScriptedChatClient();

        var result = await Planner(model).SuggestAsync(TenantId.New(), new KnowledgeGraphPlan([], [], []), true, Ct);

        result.TableDescriptions.ShouldBeEmpty();
        model.Requests.ShouldBeEmpty();
    }
}
