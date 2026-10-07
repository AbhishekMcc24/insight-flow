using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Workspace;

/// <summary>What a <see cref="ContentItem"/> points at.</summary>
public enum ContentKind
{
    /// <summary>An uploaded file (<see cref="StoredFile"/>).</summary>
    File,

    /// <summary>A dataset; the target is its latest <c>DatasetVersion</c>.</summary>
    Dataset,

    /// <summary>A <c>DataThread</c>.</summary>
    DataThread,

    /// <summary>Reserved for workbooks (TODO(dev2)).</summary>
    Workbook,

    /// <summary>Reserved for dashboards (TODO(dev2)).</summary>
    Dashboard,
}

/// <summary>
/// An entry in a workspace folder. Items are thin pointers (<see cref="Kind"/> + <see cref="TargetId"/>) so the
/// same explorer can hold files, datasets, threads and later workbooks/dashboards (unified content tree).
/// </summary>
public sealed class ContentItem
{
    private ContentItem()
    {
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid FolderId { get; private set; }

    public ItemName Name { get; private set; }

    public ContentKind Kind { get; private init; }

    /// <summary>Id of the <see cref="StoredFile"/>, dataset version or thread this item points to.</summary>
    public Guid TargetId { get; private set; }

    public string CreatedBy { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    public static ContentItem Create(Folder folder, ItemName name, ContentKind kind, Guid targetId, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        if (folder.IsDeleted)
        {
            throw new DomainRuleException("folder_deleted", "Cannot add to a deleted folder.");
        }

        if (targetId == Guid.Empty)
        {
            throw new DomainRuleException("target_required", "A content item must point at something.");
        }

        return new ContentItem
        {
            Id = Guid.CreateVersion7(),
            TenantId = folder.TenantId,
            FolderId = folder.Id,
            Name = name,
            Kind = kind,
            TargetId = targetId,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
        };
    }

    public void Rename(ItemName name)
    {
        EnsureNotDeleted();
        Name = name;
    }

    public void MoveTo(Folder target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureNotDeleted();
        if (target.TenantId != TenantId)
        {
            throw new DomainRuleException("cross_tenant_move", "Items cannot be moved between tenants.");
        }

        if (target.IsDeleted)
        {
            throw new DomainRuleException("folder_deleted", "Cannot move into a deleted folder.");
        }

        FolderId = target.Id;
    }

    /// <summary>Points a dataset item at a newer version after an extract refresh.</summary>
    public void Retarget(Guid newTargetId)
    {
        if (Kind != ContentKind.Dataset)
        {
            throw new DomainRuleException("retarget_not_supported", "Only dataset items can be retargeted.");
        }

        if (newTargetId == Guid.Empty)
        {
            throw new DomainRuleException("target_required", "A content item must point at something.");
        }

        TargetId = newTargetId;
    }

    public void SoftDelete(DateTimeOffset deletedAt) => DeletedAt ??= deletedAt;

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainRuleException("item_deleted", "The item has been deleted.");
        }
    }
}
