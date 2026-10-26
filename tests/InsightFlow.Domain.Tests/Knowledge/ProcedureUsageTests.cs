using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Tests.Knowledge;

public sealed class ProcedureUsageTests
{
    private static readonly Guid Orders = Guid.NewGuid();
    private static readonly Guid Customers = Guid.NewGuid();
    private static readonly Guid OtherDbOrders = Guid.NewGuid();

    private static readonly ProcedureTableRef[] Tables =
    [
        new(Orders, "Sales", "dbo", "orders"),
        new(Customers, "Sales", "dbo", "customers"),
        new(OtherDbOrders, "Archive", "dbo", "orders"),
    ];

    private static IReadOnlyList<Guid> Used(string sql, string database = "Sales") => ProcedureUsage.TablesUsed(sql, database, Tables);

    [Theory]
    [InlineData("SELECT * FROM orders")]
    [InlineData("SELECT * FROM dbo.orders o")]
    [InlineData("SELECT * FROM [dbo].[orders]")]
    [InlineData("select * from Sales.dbo.Orders")]
    [InlineData("SELECT * FROM Sales..orders")]
    [InlineData("EXEC('SELECT * FROM orders WHERE id = 1')")]
    public void TablesUsed_ATableInTheProceduresOwnDatabase_IsFoundHoweverItIsWritten(string sql) =>
        Used(sql).ShouldContain(Orders);

    [Fact]
    public void TablesUsed_SameNamedTableInAnotherDatabase_NeedsTheDatabaseName()
    {
        Used("SELECT * FROM orders").ShouldNotContain(OtherDbOrders);
        Used("SELECT * FROM Archive.dbo.orders").ShouldBe([OtherDbOrders]);
        Used("SELECT * FROM Archive..orders").ShouldBe([OtherDbOrders]);
    }

    [Fact]
    public void TablesUsed_OnlyTheTablesMentioned()
    {
        Used("SELECT c.name FROM customers c JOIN orders o ON o.customer_id = c.id").ShouldBe([Orders, Customers], ignoreOrder: true);
        Used("SELECT 1").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("SELECT order_id FROM shipments")]            // a column or name that merely contains the word
    [InlineData("SELECT * FROM other.orders")]                 // another schema's table
    [InlineData("SELECT * FROM preorders")]
    [InlineData("-- reads orders\nSELECT 1")]                  // comment
    [InlineData("/* orders */ SELECT 1")]
    [InlineData("SELECT @orders, #orders")]
    public void TablesUsed_LookAlikes_AreNotMatched(string sql) => Used(sql).ShouldNotContain(Orders);

    [Fact]
    public void Excerpt_KeepsTheStatementsThatTouchTheTable_WithContext_AndLeavesOutComments()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"SET @x{i} = {i};").ToList();
        lines[19] = "SELECT c.name, SUM(o.total) FROM customers c JOIN orders o ON o.customer_id = c.id WHERE o.status = 'A' -- active only";
        var text = string.Join('\n', lines);

        var excerpt = ProcedureUsage.Excerpt(text, "Sales", Tables[0], 2_000);

        excerpt.ShouldContain("JOIN orders o ON o.customer_id = c.id WHERE o.status = 'A'");
        excerpt.ShouldNotContain("active only");
        excerpt.ShouldContain("SET @x17 = 17;"); // 4 lines of context before
        excerpt.ShouldNotContain("SET @x1 = 1;"); // far from the match
    }

    [Fact]
    public void Excerpt_IsCutAtTheLimit_AndEmptyWhenTheTableIsNotMentioned()
    {
        var many = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"SELECT {i} FROM orders;"));

        ProcedureUsage.Excerpt(many, "Sales", Tables[0], 300).Length.ShouldBeLessThanOrEqualTo(301);
        ProcedureUsage.Excerpt("SELECT 1", "Sales", Tables[0], 300).ShouldBeEmpty();
    }

    [Fact]
    public void KnowledgeProcedure_Create_CutsTheDefinition_AndKeepsDistinctTableIds()
    {
        var procedure = KnowledgeProcedure.Create(
            TenantId.New(), Guid.NewGuid(), "Sales", "dbo", "usp_report", new string('x', KnowledgeProcedure.MaxDefinitionLength + 50), [Orders, Orders, Customers], DateTimeOffset.UtcNow);

        procedure.Definition.Length.ShouldBe(KnowledgeProcedure.MaxDefinitionLength);
        procedure.TableIds.Length.ShouldBe(2);
        procedure.DisplayName.ShouldBe("dbo.usp_report");
        Should.Throw<DomainRuleException>(() => KnowledgeProcedure.Create(TenantId.New(), Guid.NewGuid(), "Sales", null, " ", "x", [], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void QueryFeedback_Wrong_NeedsAReason_Right_DoesNot()
    {
        var now = DateTimeOffset.UtcNow;

        Should.Throw<DomainRuleException>(() => QueryFeedback.Create(TenantId.New(), "u", "total per customer", "SELECT 1", isRight: false, "  ", now)).Code.ShouldBe("reason_required");
        QueryFeedback.Create(TenantId.New(), "u", "total per customer", "SELECT 1", isRight: true, null, now).IsRight.ShouldBeTrue();
        QueryFeedback.Create(TenantId.New(), "u", " q ", null, isRight: false, " only active rows ", now).Reason.ShouldBe("only active rows");
        Should.Throw<DomainRuleException>(() => QueryFeedback.Create(TenantId.New(), "u", " ", null, isRight: true, null, now)).Code.ShouldBe("invalid_question");
    }
}
