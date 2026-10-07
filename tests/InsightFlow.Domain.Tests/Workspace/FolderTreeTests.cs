using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Workspace;
using InsightFlow.Testing;

namespace InsightFlow.Domain.Tests.Workspace;

public sealed class FolderTreeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = RetailModel.Tenant;

    private static Folder Child(Folder parent, string name, int parentDepth = 0) =>
        Folder.CreateChild(parent, parentDepth, ItemName.Create(name), "alice", Now);

    [Fact]
    public void CreateChild_InheritsScopeAndOwner()
    {
        var root = Folder.CreatePersonalRoot(Tenant, "alice", Now);

        var child = Child(root, "Q3");

        child.Scope.ShouldBe(FolderScope.Personal);
        child.OwnerUserId.ShouldBe("alice");
        child.ParentId.ShouldBe(root.Id);
        child.TenantId.ShouldBe(Tenant);
    }

    [Fact]
    public void CreateChild_BeyondMaxDepth_IsRejected()
    {
        var root = Folder.CreateSharedRoot(Tenant, Now);

        Should.Throw<DomainRuleException>(() => Child(root, "deep", FolderTreeRules.MaxDepth))
            .Code.ShouldBe("max_depth_exceeded");
    }

    [Fact]
    public void Roots_CannotBeRenamedMovedOrDeleted()
    {
        var root = Folder.CreateSharedRoot(Tenant, Now);
        var other = Folder.CreatePersonalRoot(Tenant, "alice", Now);

        Should.Throw<DomainRuleException>(() => root.Rename(ItemName.Create("x")));
        Should.Throw<DomainRuleException>(() => root.SoftDelete(Now));
        Should.Throw<DomainRuleException>(() => root.MoveTo(other, [other.Id], 0, 0));
    }

    [Fact]
    public void MoveTo_OwnDescendant_IsRejected()
    {
        var root = Folder.CreateSharedRoot(Tenant, Now);
        var a = Child(root, "a");
        var b = Child(a, "b", 1);

        // target b's ancestry: b, a, root
        Should.Throw<DomainRuleException>(() => a.MoveTo(b, [b.Id, a.Id, root.Id], 2, 1))
            .Code.ShouldBe("move_into_descendant");
    }

    [Fact]
    public void MoveTo_Itself_IsRejected()
    {
        var root = Folder.CreateSharedRoot(Tenant, Now);
        var a = Child(root, "a");

        Should.Throw<DomainRuleException>(() => a.MoveTo(a, [a.Id, root.Id], 1, 0)).Code.ShouldBe("move_into_descendant");
    }

    [Fact]
    public void MoveTo_WouldExceedDepth_IsRejected()
    {
        var root = Folder.CreateSharedRoot(Tenant, Now);
        var a = Child(root, "a");
        var target = Child(root, "target");

        Should.Throw<DomainRuleException>(() => a.MoveTo(target, [target.Id, root.Id], FolderTreeRules.MaxDepth - 1, 1))
            .Code.ShouldBe("max_depth_exceeded");
    }

    [Fact]
    public void MoveTo_OtherTenant_IsRejected()
    {
        var a = Child(Folder.CreateSharedRoot(Tenant, Now), "a");
        var foreignRoot = Folder.CreateSharedRoot(RetailModel.OtherTenant, Now);

        Should.Throw<DomainRuleException>(() => a.MoveTo(foreignRoot, [foreignRoot.Id], 0, 0)).Code.ShouldBe("cross_tenant_move");
    }

    [Fact]
    public void MoveTo_PersonalToShared_ReappliesScope()
    {
        var personal = Folder.CreatePersonalRoot(Tenant, "alice", Now);
        var shared = Folder.CreateSharedRoot(Tenant, Now);
        var draft = Child(personal, "Draft");

        draft.MoveTo(shared, [shared.Id], 0, 0);

        draft.ParentId.ShouldBe(shared.Id);
        draft.Scope.ShouldBe(FolderScope.Shared);
        draft.OwnerUserId.ShouldBeNull();
    }

    [Fact]
    public void SoftDelete_ThenAddChild_IsRejected()
    {
        var a = Child(Folder.CreateSharedRoot(Tenant, Now), "a");
        a.SoftDelete(Now);

        a.IsDeleted.ShouldBeTrue();
        Should.Throw<DomainRuleException>(() => Child(a, "b", 1)).Code.ShouldBe("folder_deleted");
        Should.Throw<DomainRuleException>(() => ContentItem.Create(a, ItemName.Create("f.csv"), ContentKind.File, Guid.NewGuid(), "alice", Now));
    }

    [Fact]
    public void ContentItem_Retarget_OnlyForDatasets()
    {
        var folder = Folder.CreateSharedRoot(Tenant, Now);
        var file = ContentItem.Create(folder, ItemName.Create("f.csv"), ContentKind.File, Guid.NewGuid(), "alice", Now);
        var dataset = ContentItem.Create(folder, ItemName.Create("Sales"), ContentKind.Dataset, Guid.NewGuid(), "alice", Now);
        var newVersion = Guid.NewGuid();

        Should.Throw<DomainRuleException>(() => file.Retarget(newVersion));
        dataset.Retarget(newVersion);

        dataset.TargetId.ShouldBe(newVersion);
    }

    [Fact]
    public void ContentItem_MoveToOtherTenant_IsRejected()
    {
        var item = ContentItem.Create(Folder.CreateSharedRoot(Tenant, Now), ItemName.Create("f.csv"), ContentKind.File, Guid.NewGuid(), "alice", Now);

        Should.Throw<DomainRuleException>(() => item.MoveTo(Folder.CreateSharedRoot(RetailModel.OtherTenant, Now)));
    }

    [Fact]
    public void StoredFile_BlobPath_IsBuiltFromIdsOnly()
    {
        var id = StoredFile.NewId();

        var file = StoredFile.Create(id, Tenant, ItemName.Create("..sneaky name.csv"), "text/csv", 42, new string('A', 64), "alice", Now);

        file.BlobPath.ShouldBe($"tenants/{Tenant}/files/{id}");
        file.Sha256.ShouldBe(new string('a', 64));
        file.OriginalName.ShouldBe("..sneaky name.csv");
    }

    [Theory]
    [InlineData("data.csv", TabularFormat.Csv)]
    [InlineData("DATA.TSV", TabularFormat.Csv)]
    [InlineData("book.xlsx", TabularFormat.Excel)]
    [InlineData("facts.parquet", TabularFormat.Parquet)]
    public void DetectTabularFormat_KnownExtensions(string name, TabularFormat expected)
    {
        StoredFile.DetectTabularFormat(ItemName.Create(name)).ShouldBe(expected);
    }

    [Fact]
    public void DetectTabularFormat_Other_ReturnsNull()
    {
        StoredFile.DetectTabularFormat(ItemName.Create("photo.png")).ShouldBeNull();
    }
}
