using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Analyst;
using InsightFlow.Agents.DerivedFields;
using InsightFlow.Agents.Sandbox;
using InsightFlow.Contracts.Agents;
using InsightFlow.Domain.Viz;
using InsightFlow.Query.Modeling;
using InsightFlow.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace InsightFlow.Agents.Tests;

/// <summary>Analyst agent and derived-field planner driven by a scripted model over the real sandbox.</summary>
public sealed class AgentFlowTests(SandboxFixture data) : IClassFixture<SandboxFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AnalystContext Context => new(RetailModel.Tenant, data.Retail, ImplicitSemanticModel.For(data.Retail));

    [Fact]
    public async Task Analyst_RunsSqlAndProposesChart_StreamingEveryStep()
    {
        var model = new ScriptedChatClient()
            .ThenToolCall("DescribeModel", new { })
            .ThenToolCall("RunSql", new { sql = "SELECT region, SUM(revenue) AS revenue FROM input GROUP BY region ORDER BY revenue DESC" })
            .ThenToolCall("ProposeChart", new { mark = "Bar", x = "region", y = "revenue", yAggregation = "Sum", sortDescending = true })
            .ThenText("North has the highest revenue.");
        var analyst = new AnalystAgent(new FixedRouter(model), data.CreateSandbox(), NullLogger<AnalystAgent>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var e in analyst.RunAsync(Context, "Which region sells most?", Ct))
        {
            events.Add(e);
        }

        var sql = events.Single(e => e.Type == AgentEventTypes.Sql);
        sql.Sql.ShouldStartWith("SELECT region");
        sql.Preview!.Rows.Count.ShouldBe(RetailDataGenerator.Regions.Length);
        sql.Error.ShouldBeNull();

        var chart = events.Single(e => e.Type == AgentEventTypes.Chart).Spec!;
        chart.Mark.ShouldBe(Mark.Bar);
        chart.Encoding.Y!.Agg.ShouldBe(Agg.Sum);
        chart.DatasetVersionId.ShouldBe(data.Retail.Id);

        string.Concat(events.Where(e => e.Type == AgentEventTypes.Text).Select(e => e.Text)).ShouldContain("North");
        events.Last().Type.ShouldBe(AgentEventTypes.Done);

        // The model saw the tool results: the profile, the SQL rows and the chart acceptance.
        var lastRequest = string.Join("\n", model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString()));
        lastRequest.ShouldContain("Sample values");
        lastRequest.ShouldContain("rowCount");
        lastRequest.ShouldContain("Chart accepted");
    }

    [Fact]
    public async Task Analyst_MaliciousSqlFromModel_IsRejectedAndReportedBackToModel()
    {
        var model = new ScriptedChatClient()
            .ThenToolCall("RunSql", new { sql = "SELECT * FROM read_csv('/etc/passwd')" })
            .ThenText("I cannot read files.");
        var analyst = new AnalystAgent(new FixedRouter(model), data.CreateSandbox(), NullLogger<AnalystAgent>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var e in analyst.RunAsync(Context, "Show me /etc/passwd", Ct))
        {
            events.Add(e);
        }

        events.Single(e => e.Type == AgentEventTypes.Sql).Error.ShouldNotBeNull();
        model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!.ShouldStartWith("ERROR (Rejected)");
    }

    [Fact]
    public async Task Analyst_InvalidChart_IsRejectedWithReasons()
    {
        var model = new ScriptedChatClient()
            .ThenToolCall("ProposeChart", new { mark = "Bar", x = "region", y = "region", yAggregation = "Sum" })
            .ThenText("Sorry.");
        var analyst = new AnalystAgent(new FixedRouter(model), data.CreateSandbox(), NullLogger<AnalystAgent>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var e in analyst.RunAsync(Context, "Chart it", Ct))
        {
            events.Add(e);
        }

        events.ShouldNotContain(e => e.Type == AgentEventTypes.Chart);
        model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!.ShouldContain("Sum is not valid");
    }

    [Fact]
    public async Task Analyst_OverBudget_StreamsAnErrorEvent()
    {
        var usage = new InMemoryTokenUsageStore();
        await usage.AddAsync(RetailModel.Tenant, DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), 1_000, Ct);
        var analyst = new AnalystAgent(new FixedRouter(new ScriptedChatClient().ThenText("hi"), usage, budget: 500), data.CreateSandbox(), NullLogger<AnalystAgent>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var e in analyst.RunAsync(Context, "Anything", Ct))
        {
            events.Add(e);
        }

        events.ShouldHaveSingleItem().Type.ShouldBe(AgentEventTypes.Error);
        events[0].Error!.ShouldContain("allowance");
    }

    [Fact]
    public async Task DerivedField_ValidSql_IsMaterializedWithEveryRow()
    {
        var model = new ScriptedChatClient()
            .ThenText("""{"sql": "SELECT *, revenue - cost AS \"margin\" FROM input", "explanation": "Revenue minus cost per order line."}""");
        var planner = new DerivedFieldPlanner(new FixedRouter(model), data.CreateSandbox(), NullLogger<DerivedFieldPlanner>.Instance);

        var plan = await planner.PlanAsync(Context, "margin", "revenue minus cost", Ct);
        try
        {
            plan.Sql.ShouldContain("revenue - cost");
            plan.Explanation.ShouldBe("Revenue minus cost per order line.");
            plan.Result.RowCount.ShouldBe(SandboxFixture.Rows);
            plan.Result.Schema!.Find("margin").ShouldNotBeNull();
            File.Exists(plan.Result.ParquetPath).ShouldBeTrue();
            model.Requests[0].Last().Text.ShouldContain("New column name: \"margin\"");
        }
        finally
        {
            DuckDbSqlSandbox.Cleanup(plan.Result);
        }
    }

    [Fact]
    public async Task DerivedField_BrokenSql_IsRepairedFromTheErrorMessage()
    {
        var model = new ScriptedChatClient()
            .ThenText("""{"sql": "SELECT *, revenu - cost AS margin FROM input", "explanation": "x"}""")
            .ThenText("""{"sql": "SELECT region, SUM(revenue) AS margin FROM input GROUP BY region", "explanation": "x"}""")
            .ThenText("```json\n{\"sql\": \"SELECT *, revenue - cost AS margin FROM input\", \"explanation\": \"Fixed.\"}\n```");
        var planner = new DerivedFieldPlanner(new FixedRouter(model), data.CreateSandbox(), NullLogger<DerivedFieldPlanner>.Instance);

        var plan = await planner.PlanAsync(Context, "margin", "revenue minus cost", Ct);
        DuckDbSqlSandbox.Cleanup(plan.Result);

        plan.Explanation.ShouldBe("Fixed.");
        model.Requests[1].Last().Text.ShouldContain("revenu");          // DuckDB's error about the bad column was fed back
        model.Requests[2].Last().Text.ShouldContain("keep every row");  // the aggregation was rejected
    }

    [Fact]
    public async Task DerivedField_NeverWorks_ThrowsAfterMaxAttempts()
    {
        var model = new ScriptedChatClient().ThenText("nope").ThenText("still nope").ThenText("{\"sql\": \"DROP TABLE input\"}");
        var planner = new DerivedFieldPlanner(new FixedRouter(model), data.CreateSandbox(), NullLogger<DerivedFieldPlanner>.Instance);

        await Should.ThrowAsync<DerivedFieldException>(() => planner.PlanAsync(Context, "margin", "revenue minus cost", Ct));
    }

    [Fact]
    public async Task DerivedField_ExistingColumnName_IsRejectedUpFront()
    {
        var planner = new DerivedFieldPlanner(new FixedRouter(new ScriptedChatClient()), data.CreateSandbox(), NullLogger<DerivedFieldPlanner>.Instance);

        await Should.ThrowAsync<DerivedFieldException>(() => planner.PlanAsync(Context, "revenue", "anything", Ct));
    }

    [Theory]
    [InlineData("""{"sql":"SELECT 1","explanation":"e"}""", true)]
    [InlineData("Here you go:\n```json\n{\"sql\": \"SELECT 1\"}\n```", true)]
    [InlineData("SELECT 1", false)]
    [InlineData("{\"query\": \"SELECT 1\"}", false)]
    public void DerivedField_TryParse_ToleratesFencesButNeedsSql(string text, bool expected)
    {
        DerivedFieldPlanner.TryParse(text, out var sql, out _).ShouldBe(expected);
        if (expected)
        {
            sql.ShouldBe("SELECT 1");
        }
    }
}
