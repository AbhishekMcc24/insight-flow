using InsightFlow.Agent.Knowledge;
using InsightFlow.Domain.Knowledge;

namespace InsightFlow.Tests.Agent;

/// <summary>Stored procedures as evidence for descriptions: the model sees how tables are really used, and only when writing descriptions.</summary>
public sealed class ProcedureEvidenceTests
{
    private static KnowledgeGraphPlan Plan() => new(
        [new GraphTable(Guid.NewGuid(), "Sales.dbo.orders", 0, [new GraphColumn(Guid.NewGuid(), "status", "char(1)")], DatabaseName: "Sales")],
        [],
        []);

    private static readonly ProcedureEvidence Evidence = new(
        "Sales.dbo.orders", "Sales.dbo.usp_open_orders", "SELECT SUM(total) FROM orders WHERE status = 'A'");

    private static string UserText(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages) =>
        string.Join('\n', messages.Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.User).Select(m => m.Text));

    [Fact]
    public void BuildMessages_Descriptions_IncludesTheProceduresAsEvidence()
    {
        var messages = KnowledgeAssistant.BuildMessages(Plan(), KnowledgeAssistMode.Descriptions, [new AssistantTurn("user", "describe orders")], null, true, [Evidence]);

        var text = UserText(messages);
        text.ShouldContain("Stored procedures that use these tables");
        text.ShouldContain("table \"Sales.dbo.orders\" in procedure Sales.dbo.usp_open_orders");
        text.ShouldContain("WHERE status = 'A'");
    }

    [Fact]
    public void BuildMessages_Relationships_IgnoresTheEvidence()
    {
        var messages = KnowledgeAssistant.BuildMessages(Plan(), KnowledgeAssistMode.Relationships, [new AssistantTurn("user", "link things")], null, true, [Evidence]);

        UserText(messages).ShouldNotContain("Stored procedures");
    }

    [Fact]
    public void BuildMessages_NoEvidence_LeavesThePromptAsItWas()
    {
        var with = UserText(KnowledgeAssistant.BuildMessages(Plan(), KnowledgeAssistMode.Descriptions, [new AssistantTurn("user", "x")], null, true, []));
        var without = UserText(KnowledgeAssistant.BuildMessages(Plan(), KnowledgeAssistMode.Descriptions, [new AssistantTurn("user", "x")], null, true));

        with.ShouldBe(without);
        with.ShouldNotContain("Stored procedures");
    }

    [Fact]
    public void Instructions_TellTheModelHowToUseTheProcedures()
    {
        var system = KnowledgeAssistant.BuildMessages(Plan(), KnowledgeAssistMode.Descriptions, [new AssistantTurn("user", "x")], null, true)
            .First(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).Text;

        system.ShouldContain("best evidence of how each table is really");
        system.ShouldContain("never invent meaning");
    }
}
