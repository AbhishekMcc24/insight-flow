using InsightFlow.Domain.Documents;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Tests.Domain.Documents;

public sealed class DocumentIngestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static DocumentIngestRun NewRun(params Guid[] items) => DocumentIngestRun.Create(TenantId.New(), "user", items.Length == 0 ? [Guid.NewGuid()] : items, Now);

    private static IngestProblem Problem(params string[] options) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "sales.xlsx", "Data", "type_conflict", "Cell B3 holds text", "Could not convert", [.. options.Select(o => new ProblemOption(o, o, o == ProblemChoices.Range))]);

    [Fact]
    public void Create_StartsPending_WithTheFixedSteps_AndNeedsBetweenOneAndMaxFiles()
    {
        var run = NewRun();

        run.Status.ShouldBe(IngestStatus.Pending);
        run.Steps.Select(s => s.Key).ShouldBe([IngestSteps.Queued, IngestSteps.Open, IngestSteps.Parse, IngestSteps.Save, IngestSteps.Ready]);
        run.Steps[0].State.ShouldBe(IngestStepState.Done);
        Should.Throw<DomainRuleException>(() => DocumentIngestRun.Create(TenantId.New(), "u", [], Now)).Code.ShouldBe("invalid_selection");
        Should.Throw<DomainRuleException>(() => DocumentIngestRun.Create(TenantId.New(), "u", Enumerable.Range(0, DocumentIngestRun.MaxFiles + 1).Select(_ => Guid.NewGuid()), Now));
    }

    [Fact]
    public void SetStep_MarksEarlierPendingStepsDone_SoTheListReadsTopToBottom()
    {
        var run = NewRun();

        run.SetStep(IngestSteps.Parse, IngestStepState.Running, "1 of 4 sheets");

        run.Steps.Single(s => s.Key == IngestSteps.Open).State.ShouldBe(IngestStepState.Done);
        run.Steps.Single(s => s.Key == IngestSteps.Parse).Detail.ShouldBe("1 of 4 sheets");
        run.Steps.Single(s => s.Key == IngestSteps.Save).State.ShouldBe(IngestStepState.Pending);
        Should.Throw<DomainRuleException>(() => run.SetStep("nope", IngestStepState.Done)).Code.ShouldBe("unknown_step");
    }

    [Fact]
    public void NeedInput_Waits_AndTheLastAnswerQueuesTheRunAgain()
    {
        var run = NewRun();
        var one = Problem(ProblemChoices.AsText, ProblemChoices.SkipSheet);
        var two = Problem(ProblemChoices.Range, ProblemChoices.SkipSheet);
        run.Start(Now);
        run.NeedInput([one, two]);

        run.Status.ShouldBe(IngestStatus.NeedsInput);
        run.Resolve(one.Id, ProblemChoices.AsText, null, Now);
        run.Status.ShouldBe(IngestStatus.NeedsInput); // one is still open
        run.Resolve(two.Id, ProblemChoices.Range, " A3:H200 ", Now);

        run.Status.ShouldBe(IngestStatus.Pending);
        run.Problems.Single(p => p.Id == two.Id).Text.ShouldBe("A3:H200");
        run.Problems.Single(p => p.Id == one.Id).Text.ShouldBeNull();
    }

    [Fact]
    public void Resolve_RefusesUnknownProblems_UnofferedChoices_AndMissingText()
    {
        var run = NewRun();
        var problem = Problem(ProblemChoices.Range, ProblemChoices.SkipSheet);
        run.NeedInput([problem]);

        Should.Throw<DomainRuleException>(() => run.Resolve(Guid.NewGuid(), ProblemChoices.SkipSheet, null, Now)).Code.ShouldBe("unknown_problem");
        Should.Throw<DomainRuleException>(() => run.Resolve(problem.Id, ProblemChoices.AsText, null, Now)).Code.ShouldBe("unknown_option");
        Should.Throw<DomainRuleException>(() => run.Resolve(problem.Id, ProblemChoices.Range, "  ", Now)).Code.ShouldBe("text_required");
        run.Status.ShouldBe(IngestStatus.NeedsInput);
    }

    [Fact]
    public void Succeed_FinishesTheRun_AndFail_MarksTheRunningStepFailed()
    {
        var ok = NewRun();
        ok.Succeed(3, 1_200, "3 tables", Now);
        var bad = NewRun();
        bad.SetStep(IngestSteps.Parse, IngestStepState.Running);
        bad.Fail("boom", Now);

        ok.Status.ShouldBe(IngestStatus.Succeeded);
        ok.Steps.All(s => s.State == IngestStepState.Done).ShouldBeTrue();
        ok.SheetCount.ShouldBe(3);
        bad.Status.ShouldBe(IngestStatus.Failed);
        bad.Steps.Single(s => s.Key == IngestSteps.Parse).State.ShouldBe(IngestStepState.Failed);
    }
}
