namespace InsightFlow.Domain.Workspace;

/// <summary>
/// Structural rules of the workspace tree, independent of who is acting (permissions are checked separately by
/// <see cref="IContentPermissionEvaluator"/>). Tree facts (ancestor ids, depths) come from the store's recursive
/// query so these rules stay pure and unit-testable.
/// </summary>
public static class FolderTreeRules
{
    /// <summary>Maximum folder depth below a root (root = 0).</summary>
    public const int MaxDepth = 32;

    public static void EnsureCanAddChild(Folder parent, int parentDepth)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.IsDeleted)
        {
            throw new DomainRuleException("folder_deleted", "Cannot add to a deleted folder.");
        }

        if (parentDepth + 1 > MaxDepth)
        {
            throw new DomainRuleException("max_depth_exceeded", $"Folders can be at most {MaxDepth} levels deep.");
        }
    }

    /// <param name="folder">The folder being moved.</param>
    /// <param name="target">The new parent.</param>
    /// <param name="targetAndAncestorIds">The target's id plus the ids of all its ancestors up to the root.</param>
    /// <param name="targetDepth">The target's depth (root = 0).</param>
    /// <param name="subtreeHeight">Height of the moved subtree (a folder with no sub-folders = 0).</param>
    public static void EnsureCanMove(Folder folder, Folder target, IReadOnlyCollection<Guid> targetAndAncestorIds, int targetDepth, int subtreeHeight)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetAndAncestorIds);

        if (folder.IsRoot)
        {
            throw new DomainRuleException("root_folder_immutable", "Root folders cannot be moved.");
        }

        if (folder.TenantId != target.TenantId)
        {
            throw new DomainRuleException("cross_tenant_move", "Folders cannot be moved between tenants.");
        }

        if (folder.IsDeleted || target.IsDeleted)
        {
            throw new DomainRuleException("folder_deleted", "Deleted folders cannot be moved or used as a target.");
        }

        if (target.Id == folder.Id || targetAndAncestorIds.Contains(folder.Id))
        {
            throw new DomainRuleException("move_into_descendant", "A folder cannot be moved into itself or one of its sub-folders.");
        }

        if (targetDepth + 1 + subtreeHeight > MaxDepth)
        {
            throw new DomainRuleException("max_depth_exceeded", $"Folders can be at most {MaxDepth} levels deep.");
        }
    }
}
