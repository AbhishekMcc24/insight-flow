using InsightFlow.Agent.Knowledge;
using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;

namespace InsightFlow.Tests.Agent;

/// <summary>Descriptions start at the database: the AI is shown the databases and may describe them.</summary>
public sealed class KnowledgeDatabaseDescriptionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KnowledgeGraphPlan Plan(string? salesDescription = null) => new(
        [
            new GraphTable(Guid.NewGuid(), "Sales.dbo.orders", 0, [new GraphColumn(Guid.NewGuid(), "id", "int", null, true)], DatabaseName: "Sales"),
            new GraphTable(Guid.NewGuid(), "Stock.dbo.items", 0, [new GraphColumn(Guid.NewGuid(), "id", "int", null, true)], DatabaseName: "Stock"),
        ],
        [],
        [],
        [new GraphDatabase(null, "Sales", salesDescription), new GraphDatabase(null, "Stock", null)]);

    [Fact]
    public void Describe_ListsTheDatabasesBeforeTheTables()
    {
        var text = KnowledgeGraphPlanner.Describe(Plan(), onlyMissing: true);

        text.ShouldStartWith("Databases");
        text.IndexOf("\"Sales\"", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("Tables:", StringComparison.Ordinal));
        text.ShouldContain("(needs description)");
    }

    [Fact]
    public async Task SuggestAsync_ReturnsDescriptionsForKnownDatabasesOnly()
    {
        var model = new ScriptedChatClient().ThenText("""
            {"databaseDescriptions":[{"database":"sales","description":"Orders and customers"},{"database":"Nope","description":"x"}],
             "tableDescriptions":[],"columnDescriptions":[],"relationships":[]}
            """);
        var planner = new KnowledgeGraphPlanner(new FixedRouter(model), NullLogger<KnowledgeGraphPlanner>.Instance);

        var result = await planner.SuggestAsync(TenantId.New(), Plan(), onlyMissing: true, Ct);

        result.DatabaseDescriptions.ShouldNotBeNull();
        result.DatabaseDescriptions.Single().ShouldBe(new DatabaseDescription("Sales", "Orders and customers")); // matched ignoring case, spelled as stored
    }

    [Fact]
    public void ParseDatabaseDescriptions_OnlyMissing_SkipsDatabasesThatAlreadyHaveOne()
    {
        const string reply = """{"databaseDescriptions":[{"database":"Sales","description":"new"},{"database":"Stock","description":"stock text"}]}""";

        var kept = KnowledgeGraphPlanner.ParseDatabaseDescriptions(reply, Plan("existing"), onlyMissing: true);

        kept.Select(d => d.Database).ShouldBe(["Stock"]);
    }

    [Fact]
    public void Validate_DescriptionsMode_KeepsOnlyDatabasesThatExist()
    {
        var parsed = new KnowledgeAssistant.ParsedAssistant(
            "ok", [], [], [], [], [new DatabaseDescription("STOCK", "Warehouse stock"), new DatabaseDescription("Ghost", "none")]);

        var result = KnowledgeAssistant.Validate(Plan(), KnowledgeAssistMode.Descriptions, parsed);

        result.Suggestions.DatabaseDescriptions.ShouldNotBeNull();
        result.Suggestions.DatabaseDescriptions.Single().ShouldBe(new DatabaseDescription("Stock", "Warehouse stock"));
    }
}
