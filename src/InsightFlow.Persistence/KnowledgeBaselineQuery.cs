using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Workspace;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Persistence;

/// <summary>
/// Loads the facts-only plan for the current tenant's datasets, used to import them as knowledge tables. Only
/// datasets in the Shared workspace are included; datasets in a user's personal folder never reach the knowledge base.
/// </summary>
public static class KnowledgeBaselineQuery
{
    public static async Task<KnowledgeGraphPlan> LoadDatasetBaselineAsync(this InsightFlowDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var items = await (
                from item in db.ContentItems
                join folder in db.Folders on item.FolderId equals folder.Id
                where item.Kind == ContentKind.Dataset && folder.Scope == FolderScope.Shared
                orderby item.CreatedAt
                select new { item.TargetId, item.Name })
            .Take(KnowledgeGraphPlanRules.MaxTables)
            .ToListAsync(cancellationToken);

        var ids = items.Select(i => i.TargetId).Distinct().ToList();
        var versions = await db.DatasetVersions.Where(v => ids.Contains(v.Id)).ToListAsync(cancellationToken);
        var names = items.GroupBy(i => i.TargetId).ToDictionary(g => g.Key, g => g.First().Name.Value);

        // Parents are other versions (often not shown as items); lineage only links tables that are in the graph.
        return KnowledgeGraphPlanRules.Baseline(versions.OrderBy(v => v.CreatedAt), names);
    }
}
