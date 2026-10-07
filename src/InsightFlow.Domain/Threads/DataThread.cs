using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Viz;

namespace InsightFlow.Domain.Threads;

/// <summary>
/// A titled exploration history ("Data Thread") that starts at one dataset version. Its steps are
/// <see cref="ThreadNode"/>s; branching happens by adding a node whose parent is any earlier node.
/// </summary>
public sealed class DataThread
{
    public const int MaxTitleLength = 200;

    private DataThread()
    {
        Title = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public string Title { get; private set; }

    public Guid RootVersionId { get; private init; }

    public string CreatedBy { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static DataThread Start(TenantId tenant, string title, DatasetVersion root, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        if (root.TenantId != tenant)
        {
            throw new DomainRuleException("cross_tenant_thread", "A thread must start from a dataset version of the same tenant.");
        }

        return new DataThread
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            Title = NormalizeTitle(title),
            RootVersionId = root.Id,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
        };
    }

    public void Rename(string title) => Title = NormalizeTitle(title);

    private static string NormalizeTitle(string title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxTitleLength)
        {
            throw new DomainRuleException("invalid_thread_title", $"A thread title must be 1–{MaxTitleLength} characters.");
        }

        return trimmed;
    }
}

/// <summary>
/// One immutable step of a <see cref="DataThread"/>: the dataset version it shows, optionally the chart that
/// was saved and the agent's explanation. <see cref="ParentNodeId"/> forms the visible branch tree.
/// </summary>
public sealed class ThreadNode
{
    private ThreadNode()
    {
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid ThreadId { get; private init; }

    public Guid? ParentNodeId { get; private init; }

    public Guid DatasetVersionId { get; private init; }

    public VizSpec? VizSpec { get; private init; }

    /// <summary>Agent explanation shown with the step (contains no raw data rows beyond what the user saw).</summary>
    public string? Explanation { get; private init; }

    public string CreatedBy { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static ThreadNode Create(
        DataThread thread,
        ThreadNode? parent,
        DatasetVersion version,
        VizSpec? vizSpec,
        string? explanation,
        string createdBy,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);

        if (version.TenantId != thread.TenantId || (parent is not null && parent.TenantId != thread.TenantId))
        {
            throw new DomainRuleException("cross_tenant_thread", "Thread nodes must belong to the thread's tenant.");
        }

        if (parent is not null && parent.ThreadId != thread.Id)
        {
            throw new DomainRuleException("parent_in_other_thread", "A node's parent must be in the same thread.");
        }

        if (vizSpec is not null && vizSpec.DatasetVersionId != version.Id)
        {
            throw new DomainRuleException("viz_dataset_mismatch", "The saved chart must target the node's dataset version.");
        }

        return new ThreadNode
        {
            Id = Guid.CreateVersion7(),
            TenantId = thread.TenantId,
            ThreadId = thread.Id,
            ParentNodeId = parent?.Id,
            DatasetVersionId = version.Id,
            VizSpec = vizSpec,
            Explanation = explanation,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
        };
    }
}
