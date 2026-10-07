using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Workspace;

/// <summary>Which root a folder lives under.</summary>
public enum FolderScope
{
    /// <summary>Under a user's private "My Workspace" root; only the owner can see it.</summary>
    Personal,

    /// <summary>Under the tenant's "Shared" root; visible to the tenant, writable by Creators and admins.</summary>
    Shared,
}

/// <summary>
/// A folder in the in-app workspace tree (adjacency list: <see cref="ParentId"/> is null only for roots).
/// <see cref="Scope"/> and <see cref="OwnerUserId"/> are copied from the root onto every folder so that
/// permission checks and "my folders" queries need no tree walk; moves across roots re-apply them to the
/// moved subtree (see <see cref="ApplyScopeFrom"/>).
/// </summary>
public sealed class Folder
{
    public static readonly ItemName SharedRootName = ItemName.Create("Shared");
    public static readonly ItemName PersonalRootName = ItemName.Create("My Workspace");

    private Folder()
    {
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid? ParentId { get; private set; }

    public ItemName Name { get; private set; }

    public FolderScope Scope { get; private set; }

    /// <summary>Owner of a <see cref="FolderScope.Personal"/> folder; null for shared folders.</summary>
    public string? OwnerUserId { get; private set; }

    public string CreatedBy { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsRoot => ParentId is null;

    public bool IsDeleted => DeletedAt is not null;

    public static Folder CreateSharedRoot(TenantId tenant, DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = RequireTenant(tenant),
            Name = SharedRootName,
            Scope = FolderScope.Shared,
            CreatedBy = "system",
            CreatedAt = createdAt,
        };

    public static Folder CreatePersonalRoot(TenantId tenant, string ownerUserId, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        return new Folder
        {
            Id = Guid.CreateVersion7(),
            TenantId = RequireTenant(tenant),
            Name = PersonalRootName,
            Scope = FolderScope.Personal,
            OwnerUserId = ownerUserId,
            CreatedBy = ownerUserId,
            CreatedAt = createdAt,
        };
    }

    /// <summary>Creates a sub-folder. <paramref name="parentDepth"/> is the parent's distance from its root (root = 0).</summary>
    public static Folder CreateChild(Folder parent, int parentDepth, ItemName name, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        FolderTreeRules.EnsureCanAddChild(parent, parentDepth);

        return new Folder
        {
            Id = Guid.CreateVersion7(),
            TenantId = parent.TenantId,
            ParentId = parent.Id,
            Name = name,
            Scope = parent.Scope,
            OwnerUserId = parent.OwnerUserId,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
        };
    }

    public void Rename(ItemName name)
    {
        EnsureNotRoot("rename");
        EnsureNotDeleted();
        Name = name;
    }

    /// <summary>Moves this folder under <paramref name="target"/>. Callers supply tree facts from the store; rules live in <see cref="FolderTreeRules"/>.</summary>
    public void MoveTo(Folder target, IReadOnlyCollection<Guid> targetAndAncestorIds, int targetDepth, int subtreeHeight)
    {
        FolderTreeRules.EnsureCanMove(this, target, targetAndAncestorIds, targetDepth, subtreeHeight);
        ParentId = target.Id;
        ApplyScopeFrom(target);
    }

    /// <summary>Copies scope and owner from the new ancestor; applied to every folder of a moved subtree.</summary>
    public void ApplyScopeFrom(Folder ancestor)
    {
        ArgumentNullException.ThrowIfNull(ancestor);
        Scope = ancestor.Scope;
        OwnerUserId = ancestor.OwnerUserId;
    }

    public void SoftDelete(DateTimeOffset deletedAt)
    {
        EnsureNotRoot("delete");
        DeletedAt ??= deletedAt;
    }

    private void EnsureNotRoot(string action)
    {
        if (IsRoot)
        {
            throw new DomainRuleException("root_folder_immutable", $"Root folders cannot be {action}d.");
        }
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainRuleException("folder_deleted", "The folder has been deleted.");
        }
    }

    private static TenantId RequireTenant(TenantId tenant) =>
        tenant.IsEmpty ? throw new DomainRuleException("tenant_required", "A folder needs a tenant.") : tenant;
}
