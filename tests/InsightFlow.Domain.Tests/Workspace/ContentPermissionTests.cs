using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Workspace;
using InsightFlow.Testing;

namespace InsightFlow.Domain.Tests.Workspace;

public sealed class ContentPermissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly ContentPermissionEvaluator Evaluator = new();
    private static readonly Folder Shared = Folder.CreateSharedRoot(RetailModel.Tenant, Now);
    private static readonly Folder AlicePersonal = Folder.CreatePersonalRoot(RetailModel.Tenant, "alice", Now);

    private static WorkspacePrincipal User(string id, TenantRole role, TenantId? tenant = null) =>
        new(tenant ?? RetailModel.Tenant, id, role);

    [Theory]
    [InlineData(TenantRole.Viewer, true, false)]
    [InlineData(TenantRole.Explorer, true, false)]
    [InlineData(TenantRole.Creator, true, true)]
    [InlineData(TenantRole.TenantAdmin, true, true)]
    public void Shared_RoleDecidesWrite(TenantRole role, bool canRead, bool canWrite)
    {
        var bob = User("bob", role);

        Evaluator.CanRead(Shared, bob).ShouldBe(canRead);
        Evaluator.CanWrite(Shared, bob).ShouldBe(canWrite);
    }

    [Fact]
    public void Personal_OwnerCanReadAndWrite_EvenAsViewer()
    {
        var alice = User("alice", TenantRole.Viewer);

        Evaluator.CanRead(AlicePersonal, alice).ShouldBeTrue();
        Evaluator.CanWrite(AlicePersonal, alice).ShouldBeTrue();
    }

    [Fact]
    public void Personal_OtherUsersIncludingAdmins_HaveNoAccess()
    {
        var admin = User("carol", TenantRole.TenantAdmin);

        Evaluator.CanRead(AlicePersonal, admin).ShouldBeFalse();
        Evaluator.CanWrite(AlicePersonal, admin).ShouldBeFalse();
    }

    [Fact]
    public void OtherTenant_HasNoAccess_EvenWithSameUserId()
    {
        var foreignAlice = User("alice", TenantRole.TenantAdmin, RetailModel.OtherTenant);

        Evaluator.CanRead(Shared, foreignAlice).ShouldBeFalse();
        Evaluator.CanRead(AlicePersonal, foreignAlice).ShouldBeFalse();
        Evaluator.CanWrite(AlicePersonal, foreignAlice).ShouldBeFalse();
    }
}
