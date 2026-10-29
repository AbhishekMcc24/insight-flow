using InsightFlow.Domain.Knowledge;

namespace InsightFlow.Tests.Domain.Knowledge;

/// <summary>Audit columns (created_user, modified_by…) say who wrote a row; they must not route a join between business tables.</summary>
public sealed class JoinPathsAuditTests
{
    private static readonly Guid Payments = Guid.NewGuid();
    private static readonly Guid Invoices = Guid.NewGuid();
    private static readonly Guid Users = Guid.NewGuid();
    private static readonly Guid Outstanding = Guid.NewGuid();

    private static JoinEdge Edge(Guid from, string fromColumn, Guid to, string toColumn, RelationshipKind kind) => new(from, fromColumn, to, toColumn, kind, 1);

    // The shape of the real knowledge base: both tables point their created_user at user_profile (ForeignKey, cheap), and the business tables
    // are linked to each other only by a "same attribute" edge (expensive).
    private static List<JoinEdge> WithAuditHub() =>
    [
        Edge(Payments, "ph_created_user", Users, "usp_user_id", RelationshipKind.ForeignKey),
        Edge(Invoices, "ih_created_user", Users, "usp_user_id", RelationshipKind.ForeignKey),
        Edge(Payments, "ph_company", Invoices, "ih_company", RelationshipKind.SameAttribute),
    ];

    [Fact]
    public void Connect_AuditHubWithADirectLink_UsesTheDirectLink()
    {
        var result = JoinPaths.Connect([Payments, Invoices], WithAuditHub());

        result.Tables.ShouldBe([Payments, Invoices]);
        result.Bridges.ShouldBeEmpty();
        result.Unconnected.ShouldBeEmpty();
    }

    [Fact]
    public void Connect_OnlyAnAuditRouteExists_LeavesTheTablesUnconnected()
    {
        var edges = WithAuditHub().Where(e => e.FromColumn != "ph_company").ToList();

        var result = JoinPaths.Connect([Payments, Invoices], edges);

        result.Unconnected.ShouldBe([Invoices]);
        result.Bridges.ShouldNotContain(Users);
    }

    [Fact]
    public void Connect_TheUserTableWasAskedFor_IsJoinedThroughItsAuditColumn()
    {
        var result = JoinPaths.Connect([Payments, Users], WithAuditHub());

        result.Tables.ShouldBe([Payments, Users]);
        result.Unconnected.ShouldBeEmpty();
    }

    [Fact]
    public void Connect_OrdinaryForeignKeyBridge_StillWorks()
    {
        var edges = new List<JoinEdge>
        {
            Edge(Payments, "ph_id", Outstanding, "mo_trx_id", RelationshipKind.ForeignKey),
            Edge(Outstanding, "mo_inv", Invoices, "ih_id", RelationshipKind.ForeignKey),
        };

        var result = JoinPaths.Connect([Payments, Invoices], edges);

        result.Tables.ShouldBe([Payments, Outstanding, Invoices]);
        result.Bridges.ShouldBe([Outstanding]);
    }

    [Fact]
    public void Connect_AuditEdgeBetweenBusinessTables_IsNotARoute()
    {
        var edges = new List<JoinEdge> { Edge(Payments, "pai_created_user", Invoices, "pad_created_user", RelationshipKind.SameAttribute) };

        JoinPaths.Connect([Payments, Invoices], edges).Unconnected.ShouldBe([Invoices]);
    }

    [Theory]
    [InlineData("mo_created_user", true)]
    [InlineData("ih_cancelled_by", true)]
    [InlineData("ph_modified_user", true)]
    [InlineData("pah_cancelled_by", true)]
    [InlineData("deleted_by", true)]
    [InlineData("modifiedBy", true)]
    [InlineData("CreatedUserId", true)]
    [InlineData("last_updated_by", true)]
    [InlineData("usp_user_id", false)]
    [InlineData("ph_customer_id", false)]
    [InlineData("mo_cancelled_yn", false)]
    [InlineData("ph_created_date", false)]
    [InlineData("assigned_user", false)]
    [InlineData("", false)]
    public void IsAuditColumn_RecognisesWhoWroteTheRow(string column, bool expected) =>
        AuditColumns.IsAuditColumn(column).ShouldBe(expected);

    [Fact]
    public void IsAuditRelationship_EitherSideCounts()
    {
        AuditColumns.IsAuditRelationship("mo_created_user", "usp_user_id").ShouldBeTrue();
        AuditColumns.IsAuditRelationship("usp_user_id", "ih_modified_by").ShouldBeTrue();
        AuditColumns.IsAuditRelationship("ph_id", "mo_trx_id").ShouldBeFalse();
    }
}
