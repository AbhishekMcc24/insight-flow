using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Viz;
using InsightFlow.Testing;

namespace InsightFlow.Domain.Tests.Threads;

public sealed class DataThreadTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly DatasetSchema Schema = new([new SchemaColumn("region", DataType.String), new SchemaColumn("revenue", DataType.Decimal)]);

    private static DatasetVersion Source(TenantId? tenant = null) =>
        DatasetVersion.CreateSource(DatasetVersion.NewId(), tenant ?? RetailModel.Tenant, Schema, 100, "alice", Now);

    private static DatasetVersion Derived(params DatasetVersion[] parents) =>
        DatasetVersion.CreateDerived(DatasetVersion.NewId(), parents, "SELECT * FROM input", "show me everything", Schema, 10, "alice", Now);

    [Fact]
    public void CreateSource_SetsTenantScopedParquetPath()
    {
        var version = Source();

        version.Kind.ShouldBe(DatasetVersionKind.Source);
        version.ParentIds.ShouldBeEmpty();
        version.ParquetPath.ShouldBe($"tenants/{RetailModel.Tenant}/extracts/{version.Id}.parquet");
    }

    [Fact]
    public void CreateExtract_HasPreviousVersionAsOnlyParent()
    {
        var source = Source();

        var refreshed = DatasetVersion.CreateExtract(DatasetVersion.NewId(), source, Schema, 120, "worker", Now);

        refreshed.Kind.ShouldBe(DatasetVersionKind.Extract);
        refreshed.ParentIds.ShouldBe([source.Id]);
        refreshed.TenantId.ShouldBe(source.TenantId);
    }

    [Fact]
    public void CreateExtract_OfDerivedVersion_IsRejected()
    {
        var derived = Derived(Source());

        Should.Throw<DomainRuleException>(() => DatasetVersion.CreateExtract(DatasetVersion.NewId(), derived, Schema, 1, "worker", Now))
            .Code.ShouldBe("extract_of_derived");
    }

    [Fact]
    public void CreateDerived_WithoutSql_IsRejected()
    {
        Should.Throw<DomainRuleException>(() =>
                DatasetVersion.CreateDerived(DatasetVersion.NewId(), [Source()], " ", null, Schema, 1, "alice", Now))
            .Code.ShouldBe("derived_requires_sql");
    }

    [Fact]
    public void CreateDerived_WithParentsFromDifferentTenants_IsRejected()
    {
        Should.Throw<DomainRuleException>(() => Derived(Source(), Source(RetailModel.OtherTenant)))
            .Code.ShouldBe("cross_tenant_lineage");
    }

    [Fact]
    public void CreateDerived_WithTooManyParents_IsRejected()
    {
        var parents = Enumerable.Range(0, DatasetVersion.MaxParents + 1).Select(_ => Source()).ToArray();

        Should.Throw<DomainRuleException>(() => Derived(parents)).Code.ShouldBe("derived_parent_count");
    }

    [Fact]
    public void LineageGraph_BranchFromAnyNode_TracksAncestorsAndBranchPoints()
    {
        // source -> a -> b
        //        \-> c        (branch from source)
        // a + c -> d          (multi-input derived)
        var source = Source();
        var a = Derived(source);
        var b = Derived(a);
        var c = Derived(source);
        var d = Derived(a, c);

        var graph = new LineageGraph([source, a, b, c, d]);

        graph.Roots.Select(v => v.Id).ShouldBe([source.Id]);
        graph.IsBranchPoint(source.Id).ShouldBeTrue();
        graph.IsBranchPoint(b.Id).ShouldBeFalse();
        graph.AncestorsOf(d.Id).ShouldBe([a.Id, c.Id, source.Id], ignoreOrder: true);
        graph.AncestorsOf(d.Id).Count.ShouldBe(3); // source reached twice, listed once
        graph.DescendantsOf(source.Id).ShouldBe([a.Id, c.Id, b.Id, d.Id], ignoreOrder: true);
    }

    [Fact]
    public void LineageGraph_MixedTenants_IsRejected()
    {
        Should.Throw<DomainRuleException>(() => new LineageGraph([Source(), Source(RetailModel.OtherTenant)]));
    }

    [Fact]
    public void ThreadNode_SavedSpecForOtherVersion_IsRejected()
    {
        var root = Source();
        var thread = DataThread.Start(RetailModel.Tenant, "Revenue by region", root, "alice", Now);
        var spec = new VizSpec(1, Guid.NewGuid(), Mark.Bar, new Encoding(new FieldRef("region"), new FieldRef("revenue", Agg.Sum)), []);

        Should.Throw<DomainRuleException>(() => ThreadNode.Create(thread, null, root, spec, null, "alice", Now))
            .Code.ShouldBe("viz_dataset_mismatch");
    }

    [Fact]
    public void ThreadNode_Branching_RecordsParentNode()
    {
        var root = Source();
        var thread = DataThread.Start(RetailModel.Tenant, "Revenue", root, "alice", Now);
        var first = ThreadNode.Create(thread, null, root, null, null, "alice", Now);

        var branchA = ThreadNode.Create(thread, first, Derived(root), null, "Filtered to North", "alice", Now);
        var branchB = ThreadNode.Create(thread, first, Derived(root), null, "Grouped by month", "alice", Now);

        branchA.ParentNodeId.ShouldBe(first.Id);
        branchB.ParentNodeId.ShouldBe(first.Id);
    }

    [Fact]
    public void ThreadNode_ParentFromOtherThread_IsRejected()
    {
        var root = Source();
        var t1 = DataThread.Start(RetailModel.Tenant, "One", root, "alice", Now);
        var t2 = DataThread.Start(RetailModel.Tenant, "Two", root, "alice", Now);
        var nodeInT1 = ThreadNode.Create(t1, null, root, null, null, "alice", Now);

        Should.Throw<DomainRuleException>(() => ThreadNode.Create(t2, nodeInT1, root, null, null, "alice", Now))
            .Code.ShouldBe("parent_in_other_thread");
    }

    [Fact]
    public void DataThread_FromOtherTenantsVersion_IsRejected()
    {
        Should.Throw<DomainRuleException>(() => DataThread.Start(RetailModel.Tenant, "x", Source(RetailModel.OtherTenant), "alice", Now));
    }
}
