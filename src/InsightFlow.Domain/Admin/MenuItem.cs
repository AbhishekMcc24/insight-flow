using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Admin;

/// <summary>
/// One entry of the navigation side panel, configured from Menu Master. Entries form a tree through
/// <see cref="ParentId"/>; <see cref="Route"/> is empty for pure grouping parents. Entries created by the product
/// carry a <see cref="SystemKey"/> and cannot be deleted, so an administrator can never remove their own way
/// back into the admin screens.
/// </summary>
public sealed class MenuItem : ITenantOwned
{
    public const int MaxNameLength = 100;
    public const int MaxIconLength = 40;
    public const int MaxRouteLength = 200;

    private MenuItem()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid? ParentId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Name of an icon in the Web icon set (see <c>Icon.razor</c>).</summary>
    public string? Icon { get; private set; }

    /// <summary>App-relative path such as <c>/workspace</c>; null for a grouping parent.</summary>
    public string? Route { get; private set; }

    public int Sequence { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Stable key of a product-defined entry; null for entries created by an administrator.</summary>
    public string? SystemKey { get; private init; }

    public bool IsSystem => SystemKey is not null;

    public static MenuItem Create(TenantId tenant, Guid? parentId, string name, string? icon, string? route, int sequence, bool isActive, string? systemKey = null)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A menu entry needs a tenant.");
        }

        var item = new MenuItem { Id = Guid.CreateVersion7(), TenantId = tenant, SystemKey = systemKey };
        item.Apply(parentId, name, icon, route, sequence, isActive);
        return item;
    }

    public void Update(Guid? parentId, string name, string? icon, string? route, int sequence, bool isActive)
    {
        if (IsSystem && !isActive)
        {
            throw new DomainRuleException("system_menu", "Built-in menu entries cannot be deactivated.");
        }

        Apply(parentId, name, icon, route, sequence, isActive);
    }

    private void Apply(Guid? parentId, string name, string? icon, string? route, int sequence, bool isActive)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > MaxNameLength)
        {
            throw new DomainRuleException("invalid_menu_name", $"A menu name must be 1–{MaxNameLength} characters.");
        }

        var trimmedIcon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();
        if (trimmedIcon is { Length: > MaxIconLength })
        {
            throw new DomainRuleException("invalid_menu_icon", $"An icon name can have at most {MaxIconLength} characters.");
        }

        var trimmedRoute = string.IsNullOrWhiteSpace(route) ? null : route.Trim();
        if (trimmedRoute is not null
            && (trimmedRoute.Length > MaxRouteLength || !trimmedRoute.StartsWith('/') || trimmedRoute.StartsWith("//", StringComparison.Ordinal)
                || trimmedRoute.Contains('\\', StringComparison.Ordinal) || trimmedRoute.Contains(':', StringComparison.Ordinal)))
        {
            throw new DomainRuleException("invalid_menu_route", "A route must be an app path that starts with a single '/'.");
        }

        if (parentId == Id)
        {
            throw new DomainRuleException("invalid_menu_parent", "A menu entry cannot be its own parent.");
        }

        ParentId = parentId;
        Name = trimmedName;
        Icon = trimmedIcon;
        Route = trimmedRoute;
        Sequence = sequence;
        IsActive = isActive;
    }
}

/// <summary>Grants one user one menu entry (the "User Form Access" assignment).</summary>
public sealed class UserMenuAccess : ITenantOwned
{
    private UserMenuAccess()
    {
    }

    public TenantId TenantId { get; private init; }

    public Guid UserId { get; private init; }

    public Guid MenuItemId { get; private init; }

    public static UserMenuAccess Create(TenantId tenant, Guid userId, Guid menuItemId) =>
        new() { TenantId = tenant, UserId = userId, MenuItemId = menuItemId };
}

/// <summary>Pure rules over the menu tree, so they can be tested without a database.</summary>
public static class MenuTreeRules
{
    /// <summary>True when making <paramref name="newParentId"/> the parent of <paramref name="itemId"/> would create a cycle.</summary>
    public static bool WouldCreateCycle(Guid itemId, Guid? newParentId, IReadOnlyDictionary<Guid, Guid?> parentById)
    {
        var current = newParentId;
        var steps = 0;
        while (current is { } id)
        {
            if (id == itemId || ++steps > parentById.Count + 1)
            {
                return true;
            }

            current = parentById.GetValueOrDefault(id);
        }

        return false;
    }

    /// <summary>The given ids plus every ancestor, so a granted child always shows under its group.</summary>
    public static HashSet<Guid> WithAncestors(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, Guid?> parentById)
    {
        var result = new HashSet<Guid>();
        foreach (var start in ids)
        {
            var current = (Guid?)start;
            while (current is { } id && parentById.ContainsKey(id) && result.Add(id))
            {
                current = parentById[id];
            }
        }

        return result;
    }
}
