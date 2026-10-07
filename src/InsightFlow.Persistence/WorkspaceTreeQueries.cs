using InsightFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Persistence;

/// <summary>
/// Recursive tree facts the workspace rules need (ancestors, depth, subtree). These are raw SQL — which bypasses
/// EF query filters — so every statement filters on <c>tenant_id</c> explicitly, and callers pass the tenant in scope.
/// </summary>
public static class WorkspaceTreeQueries
{
    /// <summary>The folder's id followed by its ancestors' ids up to the root. Empty when the folder is not the tenant's.</summary>
    public static async Task<IReadOnlyList<Guid>> GetFolderAndAncestorIdsAsync(
        this InsightFlowDbContext db, TenantId tenant, Guid folderId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var t = tenant.Value;
        return await db.Database.SqlQuery<Guid>($"""
            WITH RECURSIVE chain AS (
                SELECT id, parent_id, 0 AS depth FROM folders WHERE id = {folderId} AND tenant_id = {t}
                UNION ALL
                SELECT f.id, f.parent_id, c.depth + 1 FROM folders f JOIN chain c ON f.id = c.parent_id
                WHERE f.tenant_id = {t} AND c.depth < 64
            )
            SELECT id AS "Value" FROM chain ORDER BY depth
            """).ToListAsync(cancellationToken);
    }

    /// <summary>Ids of all live descendants (not including the folder itself).</summary>
    public static async Task<IReadOnlyList<Guid>> GetDescendantFolderIdsAsync(
        this InsightFlowDbContext db, TenantId tenant, Guid folderId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var t = tenant.Value;
        return await db.Database.SqlQuery<Guid>($"""
            WITH RECURSIVE sub AS (
                SELECT id, 0 AS depth FROM folders WHERE parent_id = {folderId} AND tenant_id = {t} AND deleted_at IS NULL
                UNION ALL
                SELECT f.id, s.depth + 1 FROM folders f JOIN sub s ON f.parent_id = s.id
                WHERE f.tenant_id = {t} AND f.deleted_at IS NULL AND s.depth < 64
            )
            SELECT id AS "Value" FROM sub
            """).ToListAsync(cancellationToken);
    }

    /// <summary>Height of the live subtree below a folder (a leaf folder = 0).</summary>
    public static async Task<int> GetSubtreeHeightAsync(
        this InsightFlowDbContext db, TenantId tenant, Guid folderId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var t = tenant.Value;
        var heights = await db.Database.SqlQuery<int>($"""
            WITH RECURSIVE sub AS (
                SELECT id, 0 AS depth FROM folders WHERE id = {folderId} AND tenant_id = {t}
                UNION ALL
                SELECT f.id, s.depth + 1 FROM folders f JOIN sub s ON f.parent_id = s.id
                WHERE f.tenant_id = {t} AND f.deleted_at IS NULL AND s.depth < 64
            )
            SELECT COALESCE(MAX(depth), 0) AS "Value" FROM sub
            """).ToListAsync(cancellationToken);
        return heights.Count == 0 ? 0 : heights[0];
    }
}
