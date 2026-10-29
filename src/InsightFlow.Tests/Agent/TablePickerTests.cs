using InsightFlow.Agent.Knowledge;

namespace InsightFlow.Tests.Agent;

public sealed class TablePickerTests
{
    private static readonly TableCandidate Invoice = new(Guid.NewGuid(), "db.order.invoice_hdr", "Invoice headers", ["ih_id", "ih_amount"]);
    private static readonly TableCandidate Payment = new(Guid.NewGuid(), "db.order.payment_hdr", null, ["ph_id"]);

    [Fact]
    public void TryParse_ObjectsAndPlainNames_AreBothAccepted()
    {
        TablePicker.TryParse("```json\n{\"tables\":[{\"name\":\"db.order.invoice_hdr\",\"reason\":\"amounts\"},\"db.order.payment_hdr\"]}\n```", out var named).ShouldBeTrue();

        named.ShouldBe([("db.order.invoice_hdr", "amounts"), ("db.order.payment_hdr", string.Empty)]);
    }

    [Fact]
    public void TryParse_NotJson_IsRejected() => TablePicker.TryParse("invoice and payment", out _).ShouldBeFalse();

    [Fact]
    public void Resolve_DropsNamesNotInTheCatalog_IgnoresCase_AndRepeats()
    {
        var picks = TablePicker.Resolve([Invoice, Payment], [("DB.ORDER.INVOICE_HDR", "a"), ("made_up", "b"), ("db.order.invoice_hdr", "again"), ("db.order.payment_hdr", "c")]);

        picks.Select(p => p.Id).ShouldBe([Invoice.Id, Payment.Id]);
    }

    [Fact]
    public void Resolve_NeverMoreThanTheLimit()
    {
        var many = Enumerable.Range(1, 30).Select(i => new TableCandidate(Guid.NewGuid(), $"t{i}", null, [])).ToList();

        TablePicker.Resolve(many, [.. many.Select(c => (c.Name, string.Empty))]).Count.ShouldBe(TablePicker.MaxPicks);
    }

    [Fact]
    public void BuildMessages_ListsNamesDescriptionsColumnsRelationshipsAndSkills()
    {
        var text = string.Join("\n", TablePicker.BuildMessages(
            "total outstanding", [Invoice], ["db.order.invoice_hdr.ih_id -> db.order.payment_hdr.ph_id"], ["Customer Outstanding Balance: db.order.invoice_hdr"], []).Select(m => m.Text));

        text.ShouldContain("db.order.invoice_hdr");
        text.ShouldContain("Invoice headers");
        text.ShouldContain("ih_amount");
        text.ShouldContain("-> db.order.payment_hdr");
        text.ShouldContain("Customer Outstanding Balance");
        text.ShouldContain("Question: total outstanding");
    }
}
