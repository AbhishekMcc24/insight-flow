namespace InsightFlow.Domain.Threads;

/// <summary>
/// Read-only view over a set of <see cref="DatasetVersion"/>s for lineage questions: what a version was derived
/// from, what was derived from it, and where branches start. The DAG is acyclic by construction (parents must
/// exist before children and versions are immutable); this type still guards against corrupted input.
/// </summary>
public sealed class LineageGraph
{
    private readonly Dictionary<Guid, DatasetVersion> _versions;
    private readonly Dictionary<Guid, List<Guid>> _children = [];

    public LineageGraph(IEnumerable<DatasetVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        _versions = versions.ToDictionary(v => v.Id);

        if (_versions.Values.Select(v => v.TenantId).Distinct().Count() > 1)
        {
            throw new DomainRuleException("cross_tenant_lineage", "A lineage graph cannot mix tenants.");
        }

        foreach (var version in _versions.Values)
        {
            foreach (var parent in version.ParentIds)
            {
                if (!_children.TryGetValue(parent, out var list))
                {
                    _children[parent] = list = [];
                }

                list.Add(version.Id);
            }
        }
    }

    public DatasetVersion this[Guid id] => _versions[id];

    /// <summary>Versions without parents in this graph.</summary>
    public IEnumerable<DatasetVersion> Roots =>
        _versions.Values.Where(v => v.ParentIds.All(p => !_versions.ContainsKey(p)));

    /// <summary>Direct children of a version.</summary>
    public IReadOnlyList<Guid> ChildrenOf(Guid id) =>
        _children.TryGetValue(id, out var list) ? list : [];

    /// <summary>True when more than one version was derived from <paramref name="id"/> (a branch point).</summary>
    public bool IsBranchPoint(Guid id) => ChildrenOf(id).Count > 1;

    /// <summary>All ancestors (breadth-first, nearest first), excluding the version itself.</summary>
    public IReadOnlyList<Guid> AncestorsOf(Guid id) => Walk(id, v => _versions.TryGetValue(v, out var dv) ? dv.ParentIds : []);

    /// <summary>All descendants (breadth-first, nearest first), excluding the version itself.</summary>
    public IReadOnlyList<Guid> DescendantsOf(Guid id) => Walk(id, ChildrenOf);

    private static List<Guid> Walk(Guid start, Func<Guid, IReadOnlyList<Guid>> next)
    {
        var seen = new HashSet<Guid> { start };
        var order = new List<Guid>();
        var queue = new Queue<Guid>(next(start));

        while (queue.TryDequeue(out var current))
        {
            if (current == start)
            {
                throw new DomainRuleException("lineage_cycle", "The lineage graph contains a cycle.");
            }

            if (!seen.Add(current))
            {
                continue;
            }

            order.Add(current);
            foreach (var n in next(current))
            {
                queue.Enqueue(n);
            }
        }

        return order;
    }
}
