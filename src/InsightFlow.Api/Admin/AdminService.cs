using InsightFlow.Contracts.Admin;
using InsightFlow.Domain.Admin;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InsightFlow.Api.Admin;

/// <summary>
/// Application logic of the three administration screens (User Config, Menu Master, User Form Access) and of the
/// side-panel navigation every signed-in user loads. Administration methods are reached only through endpoints that
/// require the <see cref="InsightFlowPolicies.CanAdministerTenant"/> policy; the tenant always comes from the caller.
/// </summary>
public sealed partial class AdminService(
    InsightFlowDbContext db,
    ITenantContext caller,
    TimeProvider clock,
    ILogger<AdminService> logger)
{
    /// <summary>The menu entries every tenant starts with (stable keys, so they can be found and never deleted).</summary>
    private static readonly (string Key, string Name, string Icon, string? Route, string? ParentKey)[] DefaultMenus =
    [
        ("admin.users", "User Config", "users", "/admin/users", null),
        ("admin.menus", "Menu Master", "menu", "/admin/menus", null),
        ("admin.access", "User Form Access", "key", "/admin/access", null),
        ("nav.home", "Home", "home", "/", null),
        ("nav.workspace", "Workspace", "folder", "/workspace", null),
        ("nav.chart", "Playground", "squares", "/playground", null),
        ("nav.query", "Query", "search", "/query", null),
        ("skills", "Skill Configuration", "squares", null, null),
        ("skills.categories", "Skill Category", "menu", "/skills/categories", "skills"),
        ("skills.config", "Skills", "settings", "/skills/config", "skills"),
        ("knowledge", "Knowledge", "graph", null, null),
        ("knowledge.config", "Knowledge Base Configuration", "settings", "/knowledge/config", "knowledge"),
    ];

    private TenantId Tenant => new(caller.TenantId ?? throw new InvalidOperationException("No tenant in scope."));

    private string CallerUserName => (caller.UserId ?? throw new InvalidOperationException("No user in scope.")).ToLowerInvariant();

    private bool CallerIsSysAdmin => caller.Roles.Contains(InsightFlowRoles.TenantAdmin);

    // ---------------------------------------------------------------------------------------------
    // Users
    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var users = await db.AppUsers.OrderBy(u => u.DisplayName).ThenBy(u => u.UserName).ToListAsync(cancellationToken);
        return users.Select(ToDto).ToList();
    }

    public async Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        PasswordHasher.Validate(request.Password);
        var role = ParseRole(request.Role);
        var userName = AppUser.NormalizeUserName(request.UserName);
        if (await db.AppUsers.AnyAsync(u => u.UserName == userName, cancellationToken))
        {
            throw ApiProblemException.Conflict("A user with that user name already exists.");
        }

        var user = AppUser.Create(Tenant, userName, request.DisplayName, request.Email, role, PasswordHasher.Hash(request.Password), clock.GetUtcNow());
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        LogUserCreated(logger, user.Id, role);
        return ToDto(user);
    }

    public async Task<UserDto> UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await LoadUserAsync(userId, cancellationToken);
        var role = ParseRole(request.Role);
        if (user.UserName == CallerUserName && (!request.IsActive || role != TenantRole.TenantAdmin))
        {
            throw ApiProblemException.Conflict("You cannot deactivate yourself or remove your own Sys-admin role.");
        }

        user.Update(request.DisplayName, request.Email, role, request.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        LogUserUpdated(logger, user.Id, role, request.IsActive);
        return ToDto(user);
    }

    public async Task SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken)
    {
        var user = await LoadUserAsync(userId, cancellationToken);
        user.SetPasswordHash(PasswordHasher.Hash(password));
        await db.SaveChangesAsync(cancellationToken);
        LogPasswordSet(logger, user.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // Menu master
    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<MenuItemDto>> ListMenusAsync(CancellationToken cancellationToken)
    {
        await EnsureDefaultMenusAsync(cancellationToken);
        var items = await db.MenuItems.OrderBy(m => m.Sequence).ThenBy(m => m.Name).ToListAsync(cancellationToken);
        return items.Select(ToDto).ToList();
    }

    public async Task<MenuItemDto> CreateMenuAsync(SaveMenuItemRequest request, CancellationToken cancellationToken)
    {
        await EnsureDefaultMenusAsync(cancellationToken);
        await RequireParentAsync(request.ParentId, itemId: null, cancellationToken);
        var item = MenuItem.Create(Tenant, request.ParentId, request.Name, request.Icon, request.Route, request.Sequence, request.IsActive);
        db.MenuItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        LogMenuSaved(logger, item.Id);
        return ToDto(item);
    }

    public async Task<MenuItemDto> UpdateMenuAsync(Guid menuId, SaveMenuItemRequest request, CancellationToken cancellationToken)
    {
        var item = await LoadMenuAsync(menuId, cancellationToken);
        await RequireParentAsync(request.ParentId, item.Id, cancellationToken);
        item.Update(request.ParentId, request.Name, request.Icon, request.Route, request.Sequence, request.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        LogMenuSaved(logger, item.Id);
        return ToDto(item);
    }

    public async Task DeleteMenuAsync(Guid menuId, CancellationToken cancellationToken)
    {
        var item = await LoadMenuAsync(menuId, cancellationToken);
        if (item.IsSystem)
        {
            throw ApiProblemException.Conflict("Built-in menu entries cannot be deleted.");
        }

        if (await db.MenuItems.AnyAsync(m => m.ParentId == item.Id, cancellationToken))
        {
            throw ApiProblemException.Conflict("This menu entry has children. Move or delete them first.");
        }

        db.MenuItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        LogMenuDeleted(logger, menuId);
    }

    // ---------------------------------------------------------------------------------------------
    // User form access
    // ---------------------------------------------------------------------------------------------

    public async Task<UserMenuAccessDto> GetUserAccessAsync(Guid userId, CancellationToken cancellationToken)
    {
        await LoadUserAsync(userId, cancellationToken);
        var ids = await db.UserMenuAccesses.Where(a => a.UserId == userId).Select(a => a.MenuItemId).ToListAsync(cancellationToken);
        return new UserMenuAccessDto(ids);
    }

    public async Task<UserMenuAccessDto> SetUserAccessAsync(Guid userId, UserMenuAccessDto request, CancellationToken cancellationToken)
    {
        var user = await LoadUserAsync(userId, cancellationToken);
        var wanted = request.MenuItemIds.ToHashSet();
        var known = await db.MenuItems.Where(m => wanted.Contains(m.Id)).Select(m => m.Id).ToListAsync(cancellationToken);
        if (known.Count != wanted.Count)
        {
            throw new ApiProblemException("One or more menu entries do not exist.");
        }

        var existing = await db.UserMenuAccesses.Where(a => a.UserId == userId).ToListAsync(cancellationToken);
        db.UserMenuAccesses.RemoveRange(existing.Where(a => !wanted.Contains(a.MenuItemId)));
        var already = existing.Select(a => a.MenuItemId).ToHashSet();
        db.UserMenuAccesses.AddRange(wanted.Where(id => !already.Contains(id)).Select(id => UserMenuAccess.Create(Tenant, userId, id)));
        await db.SaveChangesAsync(cancellationToken);
        LogAccessSet(logger, user.Id, wanted.Count);
        return new UserMenuAccessDto(wanted.ToList());
    }

    // ---------------------------------------------------------------------------------------------
    // Navigation for the signed-in caller
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The active menu entries the caller may see: everything for a Sys-admin (so they can never lock themselves out),
    /// otherwise the entries granted to their user record plus the parents needed to show them.
    /// </summary>
    public async Task<IReadOnlyList<MenuItemDto>> GetMyNavigationAsync(CancellationToken cancellationToken)
    {
        await EnsureDefaultMenusAsync(cancellationToken);
        var active = await db.MenuItems.Where(m => m.IsActive).OrderBy(m => m.Sequence).ThenBy(m => m.Name).ToListAsync(cancellationToken);
        if (CallerIsSysAdmin)
        {
            return active.Select(ToDto).ToList();
        }

        var userName = CallerUserName;
        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.UserName == userName && u.IsActive, cancellationToken);
        if (user is null)
        {
            return [];
        }

        var granted = await db.UserMenuAccesses.Where(a => a.UserId == user.Id).Select(a => a.MenuItemId).ToListAsync(cancellationToken);
        var parents = active.ToDictionary(m => m.Id, m => m.ParentId);
        var visible = MenuTreeRules.WithAncestors(granted.Where(parents.ContainsKey), parents);
        return active.Where(m => visible.Contains(m.Id)).Select(ToDto).ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Creates the built-in entries once per tenant. A concurrent first call losing the race is harmless.</summary>
    private async Task EnsureDefaultMenusAsync(CancellationToken cancellationToken)
    {
        var existing = await db.MenuItems.Where(m => m.SystemKey != null).Select(m => new { m.SystemKey, m.Id }).ToListAsync(cancellationToken);
        var ids = existing.ToDictionary(e => e.SystemKey!, e => e.Id);

        // The old "Chart" entry became the Playground; a tenant that still has the original name and route is moved over (a renamed one is left alone).
        // This runs even when no entry is missing, since the entry itself already exists.
        if (await db.MenuItems.FirstOrDefaultAsync(m => m.SystemKey == "nav.chart" && m.Name == "Chart" && m.Route == "/slice", cancellationToken) is { } legacy)
        {
            legacy.Update(legacy.ParentId, "Playground", "squares", "/playground", legacy.Sequence, legacy.IsActive);
            await db.SaveChangesAsync(cancellationToken);
        }

        var missing = DefaultMenus.Where(d => !ids.ContainsKey(d.Key)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        // Skill Configuration sits above Knowledge: when a tenant that already has the older entries gets it, everything from Knowledge down moves.

        var sequenceOf = new Dictionary<string, int>(StringComparer.Ordinal);
        if (missing.Any(m => m.Key == "skills") && ids.TryGetValue("knowledge", out var knowledgeId))
        {
            var first = (await db.MenuItems.FirstAsync(m => m.Id == knowledgeId, cancellationToken)).Sequence;
            var below = await db.MenuItems.Where(m => m.Sequence >= first).ToListAsync(cancellationToken);
            foreach (var menu in below)
            {
                menu.Update(menu.ParentId, menu.Name, menu.Icon, menu.Route, menu.Sequence + 3, menu.IsActive);
            }

            sequenceOf["skills"] = first;
            sequenceOf["skills.categories"] = first + 1;
            sequenceOf["skills.config"] = first + 2;
        }

        // Parents are listed before their children, so a parent created in this pass already has its id.
        foreach (var (key, name, icon, route, parentKey) in missing)
        {
            var parentId = parentKey is not null && ids.TryGetValue(parentKey, out var pid) ? pid : (Guid?)null;
            var sequence = sequenceOf.TryGetValue(key, out var fixedSequence) ? fixedSequence : Array.FindIndex(DefaultMenus, d => d.Key == key) + 1;
            var item = MenuItem.Create(Tenant, parentId, name, icon, route, sequence, true, key);
            ids[key] = item.Id;
            db.MenuItems.Add(item);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task RequireParentAsync(Guid? parentId, Guid? itemId, CancellationToken cancellationToken)
    {
        if (parentId is null)
        {
            return;
        }

        var parents = await db.MenuItems.ToDictionaryAsync(m => m.Id, m => m.ParentId, cancellationToken);
        if (!parents.ContainsKey(parentId.Value))
        {
            throw new ApiProblemException("The parent menu entry does not exist.");
        }

        if (itemId is { } id && MenuTreeRules.WouldCreateCycle(id, parentId, parents))
        {
            throw ApiProblemException.Conflict("A menu entry cannot be moved under itself or one of its children.");
        }
    }

    private async Task<AppUser> LoadUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.AppUsers.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken) ?? throw ApiProblemException.NotFound("The user");

    private async Task<MenuItem> LoadMenuAsync(Guid menuId, CancellationToken cancellationToken) =>
        await db.MenuItems.SingleOrDefaultAsync(m => m.Id == menuId, cancellationToken) ?? throw ApiProblemException.NotFound("The menu entry");

    private static TenantRole ParseRole(string role) => role switch
    {
        UserRoleNames.Viewer => TenantRole.Viewer,
        UserRoleNames.Editor => TenantRole.Creator,
        UserRoleNames.SysAdmin => TenantRole.TenantAdmin,
        _ => throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_role", "The role must be Viewer, Editor or SysAdmin."),
    };

    private static string RoleName(TenantRole role) => role switch
    {
        TenantRole.TenantAdmin => UserRoleNames.SysAdmin,
        TenantRole.Creator => UserRoleNames.Editor,
        _ => UserRoleNames.Viewer,
    };

    private static UserDto ToDto(AppUser u) =>
        new(u.Id, u.UserName, u.DisplayName, u.Email, RoleName(u.Role), u.IsActive, u.PasswordHash is not null, u.CreatedAt);

    private static MenuItemDto ToDto(MenuItem m) =>
        new(m.Id, m.ParentId, m.Name, m.Icon, m.Route, m.Sequence, m.IsActive, m.IsSystem);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} created with role {Role}")]
    private static partial void LogUserCreated(ILogger logger, Guid userId, TenantRole role);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} updated (role {Role}, active {IsActive})")]
    private static partial void LogUserUpdated(ILogger logger, Guid userId, TenantRole role, bool isActive);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password set for user {UserId}")]
    private static partial void LogPasswordSet(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Menu entry {MenuId} saved")]
    private static partial void LogMenuSaved(ILogger logger, Guid menuId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Menu entry {MenuId} deleted")]
    private static partial void LogMenuDeleted(ILogger logger, Guid menuId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Menu access of user {UserId} set to {Count} entries")]
    private static partial void LogAccessSet(ILogger logger, Guid userId, int count);
}
