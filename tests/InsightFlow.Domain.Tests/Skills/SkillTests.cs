using InsightFlow.Domain.Skills;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Tests.Skills;

public sealed class SkillTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static SkillContent Content(string name = "Outstanding amount", string instructions = "Outstanding is invoice total minus receipts.") =>
        new(name, "How outstanding is worked out", ["invoice", "receivable"], null, null, SkillDisclosure.Always, instructions);

    [Fact]
    public void Create_NewSkill_IsOnAtVersionOne()
    {
        var (skill, first) = Skill.Create(Tenant, Guid.NewGuid(), Content(), "roy", Now);

        skill.IsEnabled.ShouldBeTrue();
        skill.ActiveVersionId.ShouldBe(first.Id);
        first.Number.ShouldBe(1);
        first.Label.ShouldBe("1.0.0");
    }

    [Fact]
    public void AddVersion_AnEdit_CreatesANewActiveVersionAndKeepsTheOld()
    {
        var (skill, first) = Skill.Create(Tenant, Guid.NewGuid(), Content(), "roy", Now);

        var second = skill.AddVersion(Content(instructions: "Credit notes also reduce it."), "roy", "credit notes", Now.AddHours(1));

        second.Number.ShouldBe(2);
        second.Label.ShouldBe("1.1.0");
        skill.ActiveVersionId.ShouldBe(second.Id);
        skill.LatestNumber.ShouldBe(2);
        first.Instructions.ShouldBe("Outstanding is invoice total minus receipts.");
    }

    [Fact]
    public void Activate_AnEarlierVersion_MakesItActiveWithoutANewVersion()
    {
        var (skill, first) = Skill.Create(Tenant, Guid.NewGuid(), Content(), "roy", Now);
        skill.AddVersion(Content(name: "Outstanding v2"), "roy", null, Now.AddHours(1));

        skill.Activate(first, Now.AddHours(2));

        skill.ActiveVersionId.ShouldBe(first.Id);
        skill.Name.ShouldBe("Outstanding amount");
        skill.LatestNumber.ShouldBe(2);
    }

    [Fact]
    public void SetEnabled_SwitchingOff_DoesNotCreateAVersion()
    {
        var (skill, _) = Skill.Create(Tenant, Guid.NewGuid(), Content(), "roy", Now);

        skill.SetEnabled(false, Now);

        skill.IsEnabled.ShouldBeFalse();
        skill.LatestNumber.ShouldBe(1);
    }

    [Theory]
    [InlineData("", "ok")]
    [InlineData("name", "")]
    public void AddVersion_MissingNameOrInstructions_IsRejected(string name, string instructions)
    {
        var (skill, _) = Skill.Create(Tenant, Guid.NewGuid(), Content(), "roy", Now);

        Should.Throw<DomainRuleException>(() => skill.AddVersion(Content(name, instructions), "roy", null, Now));
        skill.LatestNumber.ShouldBe(1);
    }

    [Fact]
    public void ParseTags_CommaList_IsCleanAndDistinct() =>
        SkillContent.ParseTags(" invoice, Invoice ,receivable,, ").ShouldBe(["invoice", "receivable"]);
}
