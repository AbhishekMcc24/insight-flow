using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Domain.Tests.Knowledge;

public sealed class KnowledgeGraphPlanTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    private static DatasetVersion Version(params string[] columns) =>
        DatasetVersion.CreateSource(
            DatasetVersion.NewId(), Tenant, new DatasetSchema(columns.Select(c => new SchemaColumn(c, DataType.String)).ToList()), 10, "t", Now);

    private static KnowledgeGraphPlan TwoTables(out DatasetVersion orders, out DatasetVersion customers)
    {
        orders = Version("order_id", "customer_id", "revenue");
        customers = Version("customer_id", "name");
        return KnowledgeGraphPlanRules.Baseline(
            [orders, customers],
            new Dictionary<Guid, string> { [orders.Id] = "orders", [customers.Id] = "customers" });
    }

    [Fact]
    public void Baseline_NamesTablesFromItems_AndListsColumns()
    {
        var plan = TwoTables(out _, out _);

        plan.Tables.Select(t => t.Name).ShouldBe(["orders", "customers"]);
        plan.Tables[0].Columns.Select(c => c.Name).ShouldBe(["order_id", "customer_id", "revenue"]);
        plan.Relationships.ShouldBeEmpty();
    }

    [Fact]
    public void Baseline_DuplicateNames_AreMadeUnique()
    {
        var a = Version("x");
        var b = Version("x");

        var plan = KnowledgeGraphPlanRules.Baseline([a, b], new Dictionary<Guid, string> { [a.Id] = "sales", [b.Id] = "Sales" });

        plan.Tables.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(2);
    }

    [Fact]
    public void Baseline_DerivedVersion_GetsLineageToItsParent()
    {
        var source = Version("a");
        var derived = DatasetVersion.CreateDerived(
            DatasetVersion.NewId(), [source], "SELECT * FROM input", "copy", source.Schema, 10, "t", Now);

        var plan = KnowledgeGraphPlanRules.Baseline(
            [source, derived], new Dictionary<Guid, string> { [source.Id] = "base", [derived.Id] = "child" });

        plan.Lineage.ShouldBe([new GraphLineage("child", "base")]);
    }

    [Fact]
    public void FilterRelationships_UnknownTablesAndColumns_AreDropped()
    {
        var plan = TwoTables(out _, out _);
        var good = new GraphRelationship("orders", "customer_id", "customers", "customer_id", RelationshipKind.ForeignKey, 0.9, "same key");
        var badColumn = good with { ToColumn = "does_not_exist" };
        var badTable = good with { FromTable = "ghost" };
        var self = good with { ToTable = "orders", ToColumn = "customer_id" };

        var (valid, dropped) = KnowledgeGraphPlanRules.FilterRelationships(plan, [good, badColumn, badTable, self]);

        valid.ShouldBe([good]);
        dropped.ShouldBe(3);
    }

    [Fact]
    public void FilterRelationships_ReverseDuplicates_KeepTheMostConfident()
    {
        var plan = TwoTables(out _, out _);
        var low = new GraphRelationship("orders", "customer_id", "customers", "customer_id", RelationshipKind.ForeignKey, 0.4, "a");
        var high = new GraphRelationship("customers", "customer_id", "orders", "customer_id", RelationshipKind.SameAttribute, 0.8, "b");

        var (valid, _) = KnowledgeGraphPlanRules.FilterRelationships(plan, [low, high]);

        valid.ShouldBe([high]);
    }

    [Fact]
    public void FilterRelationships_ConfidenceOutOfRange_IsClamped()
    {
        var plan = TwoTables(out _, out _);
        var wild = new GraphRelationship("orders", "customer_id", "customers", "customer_id", RelationshipKind.ForeignKey, 7.5, new string('x', 1_000));

        var (valid, _) = KnowledgeGraphPlanRules.FilterRelationships(plan, [wild]);

        valid.Single().Confidence.ShouldBe(1);
        valid.Single().Reason.Length.ShouldBe(KnowledgeGraphPlanRules.MaxReasonLength);
    }


    [Fact]
    public void KnowledgeBaseConfig_Create_RejectsNonBoltSchemes()
    {
        Should.Throw<DomainRuleException>(() => KnowledgeBaseConfig.Create(Tenant, "http://localhost:7474", "neo4j", "neo4j", false, false, true, Now))
            .Code.ShouldBe("invalid_neo4j_uri");
        Should.Throw<DomainRuleException>(() => KnowledgeBaseConfig.Create(Tenant, "file:///etc/passwd", "neo4j", "neo4j", false, false, true, Now))
            .Code.ShouldBe("invalid_neo4j_uri");
    }

    [Fact]
    public void KnowledgeBaseConfig_ChangingTheAddress_ClearsTheLastTest()
    {
        var config = KnowledgeBaseConfig.Create(Tenant, "bolt://localhost:7687", "neo4j", "neo4j", false, false, true, Now);
        config.RecordTest(true, "ok", Now);

        config.Update("neo4j+s://x.databases.neo4j.io", "neo4j", "neo4j", true, true, true, Now);

        config.LastTestSucceeded.ShouldBeNull();
        config.LastTestedAt.ShouldBeNull();
    }

    [Fact]
    public void KnowledgeBaseConfig_InvalidDatabaseName_IsRejected() =>
        Should.Throw<DomainRuleException>(() => KnowledgeBaseConfig.Create(Tenant, "bolt://h:7687", "neo4j", "db; DROP", false, false, true, Now))
            .Code.ShouldBe("invalid_neo4j_database");
}
