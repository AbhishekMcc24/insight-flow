using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Knowledge;
using InsightFlow.Agents.Skills;
using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace InsightFlow.Agents.Tests;

public sealed class SkillAgentTests
{
    private static readonly Guid Outstanding = Guid.NewGuid();
    private static readonly Guid Credit = Guid.NewGuid();

    private static ModelPriceBook Prices(Action<AiOptions>? configure = null)
    {
        var options = new AiOptions();
        configure?.Invoke(options);
        return new ModelPriceBook(Options.Create(options));
    }

    [Fact]
    public void CostOf_KnownModelWithDatedName_UsesTheLongestPrefix()
    {
        var cost = Prices().CostOf(new AiUsage(1_000_000, 500_000, 2, "gpt-4.1-2025-04-14"));

        cost.ShouldBe(2.00m + 4.00m); // $2 per million in, $8 per million out
    }

    [Fact]
    public void CostOf_MiniModel_IsNotPricedAsTheLargeOne() =>
        Prices().CostOf(new AiUsage(1_000_000, 0, 1, "gpt-4.1-mini-2025")).ShouldBe(0.40m);

    [Fact]
    public void CostOf_UnknownModel_IsNullNotAGuess() =>
        Prices().CostOf(new AiUsage(100, 100, 1, "some-new-model")).ShouldBeNull();

    [Fact]
    public void CostOf_ConfiguredPrice_WinsOverTheBuiltInOne()
    {
        var prices = Prices(o => o.Pricing["gpt-4.1"] = new ModelPrice { InputPerMillion = 1m, OutputPerMillion = 1m });

        prices.CostOf(new AiUsage(1_000_000, 1_000_000, 1, "gpt-4.1")).ShouldBe(2m);
    }

    [Fact]
    public void CostOf_NothingUsed_IsZeroEvenWithoutAModel() =>
        Prices().CostOf(AiUsage.None).ShouldBe(0m);

    [Fact]
    public void UsageTally_AddsEveryCall()
    {
        var tally = new UsageTally();
        tally.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, "a")) { ModelId = "gpt-4.1", Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 } });
        tally.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, "b")) { ModelId = "gpt-4.1", Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 5 } });

        tally.Snapshot().ShouldBe(new AiUsage(150, 15, 2, "gpt-4.1"));
    }

    [Fact]
    public void Picker_TryParse_KeepsIdsAndReasons()
    {
        SkillPicker.TryParse($"```json\n{{\"skills\":[{{\"id\":\"{Outstanding}\",\"reason\":\"asks about outstanding\"}},{{\"id\":\"not-a-guid\"}}]}}\n```", out var picks).ShouldBeTrue();

        picks.ShouldBe([(Outstanding, "asks about outstanding")]);
    }

    [Fact]
    public void Picker_TryParse_NotJson_IsRejected() => SkillPicker.TryParse("no skills", out _).ShouldBeFalse();

    [Fact]
    public void Picker_Messages_ListHeadlinesOnly_NeverInstructions()
    {
        var catalog = new[] { new SkillCatalogEntry(Outstanding, "Outstanding", "Outstanding amount", "How outstanding is worked out", ["invoice"], ["invoice_hdr"]) };

        var text = string.Join("\n", SkillPicker.BuildMessages("total outstanding", catalog, []).Select(m => m.Text));

        text.ShouldContain(Outstanding.ToString());
        text.ShouldContain("invoice_hdr");
        text.ShouldContain("Question: total outstanding");
    }

    private static KnowledgeGraphPlan Plan() => new(
        [new GraphTable(Guid.NewGuid(), "invoice_hdr", 0, []), new GraphTable(Guid.NewGuid(), "payment_hdr", 0, [])], [], [], []);

    private static SkillSuggestion Suggestion(Guid? id = null, string category = "Outstanding", IReadOnlyList<string>? tables = null, string name = "Outstanding amount") =>
        new(id, category, false, name, "desc", ["a"], tables ?? ["invoice_hdr", "made_up_table"], SkillDisclosure.WhenRelevant, "Steps.", "because");

    [Fact]
    public void Assistant_Validate_DropsTablesThatDoNotExist_AndMarksNewCategories()
    {
        var result = SkillAssistant.Validate(Plan(), ["Outstanding"], [], null, null, [Suggestion(), Suggestion(category: "Brand new", name: "Other")]);

        result[0].Tables.ShouldBe(["invoice_hdr"]);
        result[0].NewCategory.ShouldBeFalse();
        result[1].NewCategory.ShouldBeTrue();
    }

    [Fact]
    public void Assistant_Validate_UnknownSkillId_BecomesANewSkill()
    {
        var result = SkillAssistant.Validate(Plan(), ["Outstanding"], [], null, null, [Suggestion(id: Guid.NewGuid())]);

        result.Single().SkillId.ShouldBeNull();
    }

    [Fact]
    public void Assistant_Validate_FocusedOnOneSkill_KeepsOnlyThatSkill()
    {
        var existing = new[]
        {
            new SkillDraft(Outstanding, "Outstanding", "Outstanding amount", "d", [], [], "Always", "x"),
            new SkillDraft(Credit, "Credit Notes", "Credit rules", "d", [], [], "Always", "x"),
        };

        var result = SkillAssistant.Validate(Plan(), ["Outstanding", "Credit Notes"], existing, null, Outstanding, [Suggestion(id: Outstanding), Suggestion(id: Credit, name: "Credit rules"), Suggestion(name: "Brand new")]);

        result.Select(r => r.SkillId).ShouldBe([Outstanding]);
    }

    [Fact]
    public void Assistant_TryParse_ReadsSuggestionsAndDisclosure()
    {
        var json = $"{{\"reply\":\"Done\",\"suggestions\":[{{\"skillId\":null,\"category\":\"Invoice\",\"name\":\"Invoice total\",\"description\":\"d\",\"tags\":[\"x\"],\"tables\":[\"invoice_hdr\"],\"disclosure\":\"Always\",\"instructions\":\"1. Sum\",\"reason\":\"r\"}}]}}";

        SkillAssistant.TryParse(json, out var parsed).ShouldBeTrue();

        parsed.Reply.ShouldBe("Done");
        parsed.Suggestions.Single().Disclosure.ShouldBe(SkillDisclosure.Always);
        parsed.Suggestions.Single().Tables.ShouldBe(["invoice_hdr"]);
    }
}

public sealed class CostWarningTests
{
    private static ModelPriceBook Prices(Action<AiOptions>? configure = null)
    {
        var options = new AiOptions();
        configure?.Invoke(options);
        return new ModelPriceBook(Options.Create(options));
    }

    [Fact]
    public void ModelFor_RouteToAzureLarge_UsesTheDeploymentName() =>
        Prices(o =>
        {
            o.Routes[AiTask.SqlGeneration] = "azure-openai:large";
            o.AzureOpenAI.Deployments["large"] = "gpt-4.1";
        }).ModelFor("tenant-1", AiTask.SqlGeneration).ShouldBe("gpt-4.1");

    [Fact]
    public void ModelFor_TenantOverride_WinsOverTheDefaultRoute() =>
        Prices(o =>
        {
            o.Routes[AiTask.SqlGeneration] = "azure-openai:large";
            o.AzureOpenAI.Deployments["large"] = "gpt-4.1";
            o.AzureOpenAI.Deployments["small"] = "gpt-4.1-mini";
            o.TenantRoutes["tenant-1"] = new() { [AiTask.SqlGeneration] = "azure-openai:small" };
        }).ModelFor("tenant-1", AiTask.SqlGeneration).ShouldBe("gpt-4.1-mini");

    [Fact]
    public void IsLarge_PricedRequest_IsJudgedByCost()
    {
        var prices = Prices();

        prices.IsLarge(0.049m, 500_000).ShouldBeFalse();
        prices.IsLarge(0.05m, 10).ShouldBeTrue();
    }

    [Fact]
    public void IsLarge_UnpricedRequest_IsJudgedByTokens()
    {
        var prices = Prices();

        prices.IsLarge(null, 39_999).ShouldBeFalse();
        prices.IsLarge(null, 40_000).ShouldBeTrue();
    }

    [Fact]
    public void KnowledgeEstimate_GrowsWithTheNumberOfTables()
    {
        static KnowledgeGraphPlan PlanOf(int tables) => new(
            [.. Enumerable.Range(1, tables).Select(i => new GraphTable(Guid.NewGuid(), $"table_{i}", 0, [.. Enumerable.Range(1, 8).Select(c => new GraphColumn(Guid.NewGuid(), $"col_{c}", "int"))]))], [], [], []);

        var small = KnowledgeAssistant.EstimateSize(PlanOf(3), KnowledgeAssistMode.Descriptions, false, null, 0);
        var big = KnowledgeAssistant.EstimateSize(PlanOf(60), KnowledgeAssistMode.Descriptions, false, null, 0);

        big.InputTokens.ShouldBeGreaterThan(small.InputTokens * 5);
        big.OutputTokens.ShouldBeGreaterThan(small.OutputTokens * 5);
    }

    [Fact]
    public void KnowledgeEstimate_OnlyMissing_ExpectsLessOutputThanRewritingEverything()
    {
        var plan = new KnowledgeGraphPlan(
            [new GraphTable(Guid.NewGuid(), "t", 0, [new GraphColumn(Guid.NewGuid(), "a", "int", "described"), new GraphColumn(Guid.NewGuid(), "b", "int")], "described")], [], [], []);

        var all = KnowledgeAssistant.EstimateSize(plan, KnowledgeAssistMode.Descriptions, false, null, 0);
        var missing = KnowledgeAssistant.EstimateSize(plan, KnowledgeAssistMode.Descriptions, true, null, 0);

        missing.OutputTokens.ShouldBeLessThanOrEqualTo(all.OutputTokens);
    }
}

public sealed class ConversationTests
{
    private static ModelPriceBook Prices(Action<AiOptions>? configure = null)
    {
        var options = new AiOptions();
        configure?.Invoke(options);
        return new ModelPriceBook(Options.Create(options));
    }

    [Theory]
    [InlineData("gpt-4.1-2025-04-14", 1_047_576)]
    [InlineData("gpt-4o-mini", 128_000)]
    [InlineData("gpt-5-mini", 400_000)]
    [InlineData("claude-haiku-4-5", 200_000)]
    [InlineData("some-new-model", 128_000)]
    [InlineData(null, 128_000)]
    public void ContextWindowOf_KnownAndUnknownModels(string? model, int expected) =>
        Prices().ContextWindowOf(model).ShouldBe(expected);

    [Fact]
    public void ContextWindowOf_ConfiguredValue_WinsOverTheBuiltInOne() =>
        Prices(o => o.ContextWindows["gpt-4.1"] = 50_000).ContextWindowOf("gpt-4.1-2025-04-14").ShouldBe(50_000);

    [Fact]
    public void EstimateTokens_GrowsWithTheTurns()
    {
        var small = ConversationCompactor.EstimateTokens([new QueryTurn("user", "total sales")]);
        var big = ConversationCompactor.EstimateTokens([.. Enumerable.Range(0, 100).Select(_ => new QueryTurn("assistant", new string('x', 700)))]);

        big.ShouldBeGreaterThan(small * 100);
        ConversationCompactor.EstimateTokens([]).ShouldBe(0);
    }

    [Fact]
    public void Defaults_CompactAtEightyPercent() => new ConversationOptions().CompactAtFraction.ShouldBe(0.8);

    [Fact]
    public void QueryTurnDto_FollowUpDigest_IsKeptSeparateFromTheText()
    {
        var turn = new InsightFlow.Contracts.Knowledge.QueryTurnDto("assistant", "explanation\nSQL: SELECT 1", "Result: 1 row; columns: ph_id\n  PAY-1001");

        turn.Content.ShouldNotContain("PAY-1001");
        turn.Data.ShouldNotBeNull().ShouldContain("PAY-1001");
    }
}

public sealed class ConversationCeilingTests
{
    [Fact]
    public void Defaults_CeilingIs60000_SoCompactionStartsAt48000()
    {
        var options = new ConversationOptions();

        options.MaxContextTokens.ShouldBe(60_000);
        ((long)(options.MaxContextTokens!.Value * options.CompactAtFraction)).ShouldBe(48_000);
    }
}
