using InsightFlow.Domain.Admin;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Tests.Domain.Admin;

public sealed class AdminRulesTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AppUserCreate_UserNameWithMixedCase_IsStoredLowerCase()
    {
        var user = AppUser.Create(Tenant, "  Jane.Doe ", "Jane Doe", null, TenantRole.Viewer, null, Now);

        user.UserName.ShouldBe("jane.doe");
        user.IsActive.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public void AppUserCreate_InvalidUserName_Throws(string userName) =>
        Should.Throw<DomainRuleException>(() => AppUser.Create(Tenant, userName, "X", null, TenantRole.Viewer, null, Now))
            .Code.ShouldBe("invalid_user_name");

    [Fact]
    public void AppUserCreate_ExplorerRole_IsRejected() =>
        Should.Throw<DomainRuleException>(() => AppUser.Create(Tenant, "x", "X", null, TenantRole.Explorer, null, Now))
            .Code.ShouldBe("invalid_role");

    [Fact]
    public void AppUserCreate_EmailWithoutAt_IsRejected() =>
        Should.Throw<DomainRuleException>(() => AppUser.Create(Tenant, "x", "X", "nope", TenantRole.Viewer, null, Now))
            .Code.ShouldBe("invalid_email");

    [Theory]
    [InlineData("workspace")]
    [InlineData("//evil.example")]
    [InlineData("/a:b")]
    [InlineData("/a\\b")]
    public void MenuItemCreate_RouteNotAnAppPath_Throws(string route) =>
        Should.Throw<DomainRuleException>(() => MenuItem.Create(Tenant, null, "Menu", null, route, 1, true))
            .Code.ShouldBe("invalid_menu_route");

    [Fact]
    public void MenuItemUpdate_SystemEntryDeactivated_Throws()
    {
        var item = MenuItem.Create(Tenant, null, "Home", "home", "/", 1, true, "nav.home");

        Should.Throw<DomainRuleException>(() => item.Update(null, "Home", "home", "/", 1, false)).Code.ShouldBe("system_menu");
    }

    [Fact]
    public void MenuItemUpdate_OwnParent_Throws()
    {
        var item = MenuItem.Create(Tenant, null, "Menu", null, null, 1, true);

        Should.Throw<DomainRuleException>(() => item.Update(item.Id, "Menu", null, null, 1, true)).Code.ShouldBe("invalid_menu_parent");
    }

    [Fact]
    public void WouldCreateCycle_MovingUnderOwnDescendant_ReturnsTrue()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var parents = new Dictionary<Guid, Guid?> { [a] = null, [b] = a, [c] = b };

        MenuTreeRules.WouldCreateCycle(a, c, parents).ShouldBeTrue();
        MenuTreeRules.WouldCreateCycle(c, a, parents).ShouldBeFalse();
        MenuTreeRules.WouldCreateCycle(a, null, parents).ShouldBeFalse();
    }

    [Fact]
    public void WithAncestors_GrantedChild_IncludesItsGroups()
    {
        Guid root = Guid.NewGuid(), group = Guid.NewGuid(), leaf = Guid.NewGuid(), other = Guid.NewGuid();
        var parents = new Dictionary<Guid, Guid?> { [root] = null, [group] = root, [leaf] = group, [other] = null };

        var visible = MenuTreeRules.WithAncestors([leaf], parents);

        visible.ShouldBe([root, group, leaf], ignoreOrder: true);
    }
}
