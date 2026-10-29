using InsightFlow.Agent.Knowledge;
using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Tenancy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace InsightFlow.Tests.Agent;

/// <summary>The chat assistant for descriptions and relationships, driven by a scripted model (no network).</summary>
public sealed class KnowledgeAssistantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KnowledgeGraphPlan Plan(params GraphRelationship[] known) => new(
        [
            new GraphTable(Guid.NewGuid(), "dbo.orders", 0,
            [
                new GraphColumn(Guid.NewGuid(), "order_id", "int", "Order number", true),
                new GraphColumn(Guid.NewGuid(), "customer_id", "int", "The customer who placed the order"),
            ], "One row per order"),
            new GraphTable(Guid.NewGuid(), "dbo.customers", 0,
            [
                new GraphColumn(Guid.NewGuid(), "customer_id", "int", "Unique customer number", true),
                new GraphColumn(Guid.NewGuid(), "name", "varchar(100)"),
            ]),
        ],
        [],
        known);

    private static KnowledgeAssistant Assistant(ScriptedChatClient model) =>
        new(new FixedRouter(model), NullLogger<KnowledgeAssistant>.Instance);

    private static string PromptOf(ScriptedChatClient model, int request = 0) =>
        string.Join("\n", model.Requests[request].Select(m => m.Text));

    [Fact]
    public async Task Respond_Descriptions_ReturnsReplyAndOnlyDescriptionsForRealTablesAndColumns()
    {
        var model = new ScriptedChatClient().ThenText("""
            {"reply":"I drafted descriptions.",
             "tableDescriptions":[{"table":"dbo.customers","description":"People who buy"},{"table":"ghost","description":"x"}],
             "columnDescriptions":[{"table":"dbo.customers","column":"name","description":"Full name"},{"table":"dbo.customers","column":"nope","description":"x"}],
             "relationships":[{"fromTable":"dbo.orders","fromColumn":"customer_id","toTable":"dbo.customers","toColumn":"customer_id","kind":"ForeignKey","confidence":1,"reason":"must be ignored in this mode"}]}
            """);

        var result = await Assistant(model).RespondAsync(TenantId.New(), Plan(), KnowledgeAssistMode.Descriptions, [], null, onlyMissing: true, Ct);

        result.Reply.ShouldBe("I drafted descriptions.");
        result.Suggestions.TableDescriptions.Single().Table.ShouldBe("dbo.customers");
        result.Suggestions.ColumnDescriptions.Single().Column.ShouldBe("name");
        result.Suggestions.Relationships.ShouldBeEmpty();
    }

    [Fact]
    public async Task Respond_Relationships_ProposesNewLinks_AndIgnoresDescriptions()
    {
        var model = new ScriptedChatClient().ThenText("""
            {"reply":"Orders point at customers.",
             "tableDescriptions":[{"table":"dbo.customers","description":"ignored"}],
             "relationships":[{"fromTable":"dbo.orders","fromColumn":"customer_id","toTable":"dbo.customers","toColumn":"customer_id",
                               "kind":"ForeignKey","confidence":0.92,"reason":"Both describe the customer number"}]}
            """);

        var result = await Assistant(model).RespondAsync(TenantId.New(), Plan(), KnowledgeAssistMode.Relationships, [], null, true, Ct);

        result.Suggestions.Relationships.Single().Reason.ShouldBe("Both describe the customer number");
        result.Suggestions.TableDescriptions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Respond_Relationships_AlreadyKnownInventedAndMissingRemovals_AreFilteredOut()
    {
        var known = new GraphRelationship("dbo.customers", "customer_id", "dbo.orders", "customer_id", RelationshipKind.ForeignKey, 1, "declared");
        var model = new ScriptedChatClient().ThenText("""
            {"reply":"ok",
             "relationships":[
               {"fromTable":"dbo.orders","fromColumn":"customer_id","toTable":"dbo.customers","toColumn":"customer_id","kind":"ForeignKey","confidence":0.9,"reason":"already known (reverse)"},
               {"fromTable":"dbo.orders","fromColumn":"nope","toTable":"dbo.customers","toColumn":"customer_id","kind":"ForeignKey","confidence":0.9,"reason":"invented"}],
             "removeRelationships":[
               {"fromTable":"dbo.orders","fromColumn":"customer_id","toTable":"dbo.customers","toColumn":"customer_id"},
               {"fromTable":"dbo.orders","fromColumn":"order_id","toTable":"dbo.customers","toColumn":"name"}]}
            """);

        var result = await Assistant(model).RespondAsync(TenantId.New(), Plan(known), KnowledgeAssistMode.Relationships, [], null, true, Ct);

        result.Suggestions.Relationships.ShouldBeEmpty();
        result.Suggestions.DroppedRelationships.ShouldBe(1); // only the invented one is reported as discarded; a link that already exists (even reversed) is skipped quietly
        result.Suggestions.Removals!.Single().ShouldBe(known); // only the removal that matches an existing relationship survives, in either direction
    }

    [Fact]
    public async Task Respond_FirstTurn_AsksForADraft_AndShowsTheStateWithDescriptions()
    {
        var model = new ScriptedChatClient().ThenText("""{"reply":"hi"}""");

        await Assistant(model).RespondAsync(TenantId.New(), Plan(), KnowledgeAssistMode.Relationships, [], null, true, Ct);

        var prompt = PromptOf(model);
        prompt.ShouldContain("\"customer_id\" int — The customer who placed the order"); // descriptions are the evidence
        prompt.ShouldContain("Proposal currently shown to the person");
        prompt.ShouldContain("(empty)");
        prompt.ShouldContain("Read the column descriptions and propose the relationships");
        model.Requests[0].Last().Role.ShouldBe(ChatRole.User);
    }

    [Fact]
    public async Task Respond_FollowUp_SendsTheConversationAndTheProposalOnScreen_WithoutRepeatingTheFirstRequest()
    {
        var model = new ScriptedChatClient().ThenText("""{"reply":"shorter now"}""");
        var current = new KnowledgeSuggestions([new TableDescription("dbo.orders", "A long description of orders")], [], [], 0, []);
        var history = new[]
        {
            new AssistantTurn("assistant", "I drafted descriptions."),
            new AssistantTurn("user", "Make them shorter"),
        };

        await Assistant(model).RespondAsync(TenantId.New(), Plan(), KnowledgeAssistMode.Descriptions, history, current, true, Ct);

        var prompt = PromptOf(model);
        prompt.ShouldContain("- table \"dbo.orders\": A long description of orders");
        prompt.ShouldNotContain("Write descriptions for every table"); // the default first request is only for the first turn
        model.Requests[0].Last().Text.ShouldBe("Make them shorter");
        model.Requests[0].Last().Role.ShouldBe(ChatRole.User);
    }

    [Fact]
    public async Task Respond_LongConversation_KeepsOnlyTheLatestTurns()
    {
        var model = new ScriptedChatClient().ThenText("""{"reply":"ok"}""");
        var history = Enumerable.Range(0, 40).Select(i => new AssistantTurn(i % 2 == 0 ? "user" : "assistant", $"turn {i}")).Append(new AssistantTurn("user", "latest")).ToList();

        await Assistant(model).RespondAsync(TenantId.New(), Plan(), KnowledgeAssistMode.Descriptions, history, null, true, Ct);

        var texts = model.Requests[0].Select(m => m.Text).ToList();
        texts.ShouldNotContain("turn 0");
        texts.Last().ShouldBe("latest");
        (texts.Count - 2).ShouldBeLessThanOrEqualTo(KnowledgeAssistant.MaxHistoryTurns); // minus the system message and the state message
    }

    [Fact]
    public async Task Respond_NotJsonThenJson_Repairs_AndNeverJsonThrows()
    {
        var repaired = new ScriptedChatClient().ThenText("Sure thing!").ThenText("""{"reply":"fixed"}""");
        (await Assistant(repaired).RespondAsync(TenantId.New(), Plan(), KnowledgeAssistMode.Descriptions, [], null, true, Ct)).Reply.ShouldBe("fixed");

        var broken = new ScriptedChatClient().ThenText("no").ThenText("still no");
        await Should.ThrowAsync<KnowledgeGraphPlanningException>(() =>
            Assistant(broken).RespondAsync(TenantId.New(), Plan(), KnowledgeAssistMode.Descriptions, [], null, true, Ct));
    }

    [Fact]
    public async Task Respond_NoTables_DoesNotCallTheModel()
    {
        var model = new ScriptedChatClient();

        var result = await Assistant(model).RespondAsync(TenantId.New(), new KnowledgeGraphPlan([], [], []), KnowledgeAssistMode.Descriptions, [], null, true, Ct);

        result.Suggestions.TableDescriptions.ShouldBeEmpty();
        model.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Respond_ReplyMissing_FallsBackToAFriendlyDefault()
    {
        var model = new ScriptedChatClient().ThenText("""{"tableDescriptions":[]}""");

        var result = await Assistant(model).RespondAsync(TenantId.New(), Plan(), KnowledgeAssistMode.Descriptions, [], null, true, Ct);

        result.Reply.ShouldNotBeNullOrWhiteSpace();
    }
}
