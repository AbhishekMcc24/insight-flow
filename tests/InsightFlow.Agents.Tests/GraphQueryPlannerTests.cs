using InsightFlow.Agents.Knowledge;
using InsightFlow.Agents.Sandbox;
using InsightFlow.Domain.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;

namespace InsightFlow.Agents.Tests;

/// <summary>Plain-English questions to SQL through the knowledge graph, driven by a scripted model (no network).</summary>
public sealed class GraphQueryPlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid CustomersId = Guid.NewGuid();
    private static readonly Guid OrdersId = Guid.NewGuid();
    private static readonly Guid LinesId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

    private static QueryColumn Col(string name, string type = "int", bool key = false, string? description = null) => new(name, type, description, key);

    private static readonly QueryTable Customers = new(CustomersId, "customers", "shop.dbo.customers", "People who buy", [Col("id", key: true), Col("name", "varchar(100)", description: "Full name")]);
    private static readonly QueryTable Orders = new(OrdersId, "orders", "shop.dbo.orders", "One row per order", [Col("id", key: true), Col("customer_id"), Col("total", "decimal(18,2)", description: "Order value")]);
    private static readonly QueryTable Lines = new(LinesId, "lines", "shop.dbo.lines", null, [Col("company"), Col("order_no"), Col("qty")]);

    private static readonly QueryJoin OrderCustomer = new(OrdersId, "customer_id", CustomersId, "id", "ForeignKey", 1, null);

    private static GraphQueryPlanner Planner(ScriptedChatClient model) => new(new FixedRouter(model), NullLogger<GraphQueryPlanner>.Instance);

    private static Task<QueryPlan> Plan(GraphQueryPlanner planner, params QueryJoin[] joins) =>
        planner.PlanAsync(TenantId.New(), "total per customer", [Customers, Orders], joins, [], Ct);

    [Fact]
    public void BuildAliases_AreSafeIdentifiers_Unique_AndNeverReserved()
    {
        GraphQueryPlanner.BuildAliases(["Payment Hdr", "order", "order", "123abc", "$$"]).ShouldBe(["payment_hdr", "order_t", "order_t_2", "t_123abc", "t"]);
    }

    [Fact]
    public async Task PlanAsync_ValidJoinOnAGraphRelationship_ReturnsTheSqlAndTheJoinUsed()
    {
        var model = new ScriptedChatClient().ThenText("""
            {"explanation":"Order totals per customer.","sql":"SELECT c.name AS customer, SUM(o.total) AS total FROM orders o JOIN customers c ON o.customer_id = c.id GROUP BY c.name ORDER BY total DESC",
             "assumptions":["Total means the order value"],"clarification":null}
            """);

        var plan = await Plan(Planner(model), OrderCustomer);

        plan.Sql.ShouldNotBeNull().ShouldContain("JOIN customers c");
        plan.UsedJoins.ShouldBe([OrderCustomer]);
        plan.Assumptions.ShouldBe(["Total means the order value"]);
        plan.Clarification.ShouldBeNull();
    }

    [Fact]
    public async Task PlanAsync_InventedJoin_IsSentBackForRepair_ThenAccepted()
    {
        var model = new ScriptedChatClient()
            .ThenText("""{"explanation":"x","sql":"SELECT c.name FROM orders o JOIN customers c ON o.total = c.id"}""")
            .ThenText("""{"explanation":"fixed","sql":"SELECT c.name FROM orders o JOIN customers c ON o.customer_id = c.id"}""");

        var plan = await Plan(Planner(model), OrderCustomer);

        plan.Explanation.ShouldBe("fixed");
        plan.UsedJoins.ShouldBe([OrderCustomer]);
    }

    [Fact]
    public async Task PlanAsync_NeverAValidQuery_Throws()
    {
        var bad = """{"sql":"SELECT c.name FROM orders o JOIN customers c ON o.total = c.id"}""";
        var model = new ScriptedChatClient().ThenText(bad).ThenText(bad).ThenText(bad);

        var ex = await Should.ThrowAsync<KnowledgeGraphPlanningException>(() => Plan(Planner(model), OrderCustomer));

        ex.Message.ShouldContain("not a relationship");
    }

    [Fact]
    public async Task PlanAsync_AskingForAFurtherTable_IsRejectedByTheGuard()
    {
        var model = new ScriptedChatClient()
            .ThenText("""{"sql":"SELECT * FROM secret_table"}""")
            .ThenText("""{"sql":"SELECT o.total FROM orders o"}""");

        var plan = await Plan(Planner(model));

        plan.Sql.ShouldBe("SELECT o.total FROM orders o");
    }

    [Fact]
    public async Task PlanAsync_ModelAsksAQuestion_ReturnsTheClarificationAndNoSql()
    {
        var model = new ScriptedChatClient().ThenText("""{"sql":null,"clarification":"Which date range do you mean?"}""");

        var plan = await Plan(Planner(model), OrderCustomer);

        plan.Sql.ShouldBeNull();
        plan.Clarification.ShouldBe("Which date range do you mean?");
    }

    [Fact]
    public async Task PlanAsync_UnknownColumn_IsRepaired()
    {
        var model = new ScriptedChatClient()
            .ThenText("""{"sql":"SELECT o.amount FROM orders o"}""")
            .ThenText("""{"sql":"SELECT o.total FROM orders o"}""");

        var plan = await Plan(Planner(model));

        plan.Sql.ShouldBe("SELECT o.total FROM orders o");
    }

    [Fact]
    public async Task Inspect_CompositeKey_MustBeJoinedOnAllPairs()
    {
        var pairs = new[]
        {
            new QueryJoin(LinesId, "company", OrdersId, "id", "ForeignKey", 1, GroupId),
            new QueryJoin(LinesId, "order_no", OrdersId, "customer_id", "ForeignKey", 1, GroupId),
        };
        var half = await SqlGuard.SerializeAsync("SELECT l.qty FROM lines l JOIN orders o ON l.company = o.id", Ct);
        var full = await SqlGuard.SerializeAsync("SELECT l.qty FROM lines l JOIN orders o ON l.company = o.id AND l.order_no = o.customer_id", Ct);

        QuerySqlInspector.Inspect(half, [Lines, Orders], pairs).Problems.ShouldContain(p => p.Contains("composite key", StringComparison.Ordinal));
        QuerySqlInspector.Inspect(full, [Lines, Orders], pairs).Problems.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("SELECT c.name FROM orders o JOIN customers c USING (id)")]
    [InlineData("SELECT c.name FROM orders o CROSS JOIN customers c")]
    [InlineData("SELECT c.name FROM orders o NATURAL JOIN customers c")]
    [InlineData("SELECT c.name FROM orders o JOIN customers c ON o.id = c.id")]
    public async Task Inspect_JoinsThatCannotBeTiedToARelationship_AreProblems(string sql)
    {
        var tree = await SqlGuard.SerializeAsync(sql, Ct);

        QuerySqlInspector.Inspect(tree, [Orders, Customers], [OrderCustomer]).Problems.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Inspect_JoinWrittenInEitherDirection_IsAccepted()
    {
        var tree = await SqlGuard.SerializeAsync("SELECT c.name FROM customers c JOIN orders o ON c.id = o.customer_id", Ct);

        QuerySqlInspector.Inspect(tree, [Orders, Customers], [OrderCustomer]).Problems.ShouldBeEmpty();
    }

    [Fact]
    public void Describe_ListsAliasesColumnsDescriptionsAndOnlyTheAllowedJoins()
    {
        var text = GraphQueryPlanner.Describe([Customers, Orders], [OrderCustomer]);

        text.ShouldContain("- orders  [shop.dbo.orders] — One row per order");
        text.ShouldContain("total decimal(18,2) — Order value");
        text.ShouldContain("orders.customer_id = customers.id");
    }
}
