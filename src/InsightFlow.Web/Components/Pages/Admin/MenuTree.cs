using InsightFlow.Contracts.Admin;

namespace InsightFlow.Web.Components.Pages.Admin;

/// <summary>Turns the flat menu list into display order (parents before their children, siblings by sequence).</summary>
public static class MenuTree
{
    public static List<(MenuItemDto Item, int Depth)> Flatten(IReadOnlyList<MenuItemDto> items)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        var result = new List<(MenuItemDto, int)>(items.Count);
        var visited = new HashSet<Guid>();

        void Walk(Guid? parentId, int depth)
        {
            var siblings = items
                .Where(i => parentId is null ? i.ParentId is null || !ids.Contains(i.ParentId.Value) : i.ParentId == parentId)
                .OrderBy(i => i.Sequence)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var item in siblings)
            {
                if (visited.Add(item.Id))
                {
                    result.Add((item, depth));
                    Walk(item.Id, depth + 1);
                }
            }
        }

        Walk(null, 0);
        return result;
    }

    /// <summary>The ids of <paramref name="id"/> and everything below it.</summary>
    public static HashSet<Guid> Subtree(Guid id, IReadOnlyList<MenuItemDto> items)
    {
        var result = new HashSet<Guid> { id };
        var queue = new Queue<Guid>([id]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in items.Where(i => i.ParentId == current && result.Add(i.Id)))
            {
                queue.Enqueue(child.Id);
            }
        }

        return result;
    }
}
