using InsightFlow.Agent.Knowledge;

namespace InsightFlow.Tests.Agent;

public sealed class SkillSelectorTests
{
    private static SkillGuidance Skill(string name, string category, bool always, string text = "Rule.", params string[] tags) =>
        new(name, category, "description", tags, always, text);

    [Fact]
    public void Choose_AlwaysSkills_AreIncludedWhateverTheQuestion()
    {
        var chosen = SkillSelector.Choose("how many customers", [Skill("Fiscal year", "Invoice", always: true)]);

        chosen.Select(s => s.Name).ShouldBe(["Fiscal year"]);
    }

    [Fact]
    public void Choose_WhenRelevantSkill_NeedsAMatchingWord()
    {
        var skills = new[] { Skill("Outstanding amount", "Outstanding", always: false, tags: "receivable"), Skill("Credit note rules", "Credit Notes", always: false) };

        SkillSelector.Choose("show outstanding by customer", skills).Select(s => s.Name).ShouldBe(["Outstanding amount"]);
        SkillSelector.Choose("list all products", skills).ShouldBeEmpty();
    }

    [Fact]
    public void Choose_PluralWordInTheQuestion_MatchesTheSingularSkill() =>
        SkillSelector.Choose("total invoices last month", [Skill("Invoice totals", "Invoice", always: false)]).Count.ShouldBe(1);

    [Fact]
    public void Choose_OverTheBudget_KeepsTheMostRelevantFirst()
    {
        var big = new string('x', SkillSelector.MaxCharactersPerSkill);
        var skills = Enumerable.Range(1, 6).Select(i => Skill($"Always {i}", "Invoice", always: true, big)).Append(Skill("Outstanding", "Outstanding", false, "short")).ToList();

        var chosen = SkillSelector.Choose("outstanding balance", skills);

        chosen[0].Name.ShouldBe("Outstanding");
        chosen.Sum(s => s.Instructions.Length).ShouldBeLessThanOrEqualTo(SkillSelector.MaxCharacters);
    }

    [Fact]
    public void Describe_NoSkills_IsEmpty_AndSkillsAreListedWithTheirCategory()
    {
        SkillSelector.Describe([]).ShouldBeEmpty();
        SkillSelector.Describe([Skill("Outstanding amount", "Outstanding", true, "Use receipts.")]).ShouldContain("### Outstanding amount (Outstanding)");
    }
}
