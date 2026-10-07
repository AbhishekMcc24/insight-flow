using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Workspace;

namespace InsightFlow.Domain.Connections;

/// <summary>
/// "This source becomes this dataset": either a table of a saved connection or an uploaded file, the folder the
/// dataset item lives in, and the latest version produced. Each refresh creates a new immutable
/// <see cref="DatasetVersion"/> and moves <see cref="LatestVersionId"/> forward.
/// </summary>
public sealed class ExtractDefinition : ITenantOwned
{
    private ExtractDefinition()
    {
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public ItemName Name { get; private set; }

    public DataSourceKind SourceKind { get; private init; }

    /// <summary>Saved connection (database sources); null for uploaded files.</summary>
    public Guid? ConnectionProfileId { get; private init; }

    /// <summary>Source table as returned by connector discovery (e.g. <c>dbo.Orders</c>); null for files.</summary>
    public string? SourceTable { get; private init; }

    /// <summary>Uploaded file (file sources); null for database sources.</summary>
    public Guid? StoredFileId { get; private init; }

    /// <summary>Folder that receives the dataset item.</summary>
    public Guid TargetFolderId { get; private init; }

    public Guid? LatestVersionId { get; private set; }

    /// <summary>The dataset's <see cref="ContentItem"/>, created by the first successful run.</summary>
    public Guid? ContentItemId { get; private set; }

    public string CreatedBy { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static ExtractDefinition ForConnection(
        ConnectionProfile connection, string sourceTable, Folder targetFolder, ItemName name, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTable);
        return Create(connection.TenantId, connection.Kind, connection.Id, sourceTable, null, targetFolder, name, createdBy, createdAt);
    }

    public static ExtractDefinition ForFile(StoredFile file, TabularFormat format, Folder targetFolder, ItemName name, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(file);
        var kind = format switch
        {
            TabularFormat.Csv => DataSourceKind.Csv,
            TabularFormat.Excel => DataSourceKind.Excel,
            TabularFormat.Parquet => DataSourceKind.Parquet,
            _ => throw new DomainRuleException("unsupported_format", $"Format {format} cannot become a dataset."),
        };
        return Create(file.TenantId, kind, null, null, file.Id, targetFolder, name, createdBy, createdAt);
    }

    /// <summary>Records a successful run: the new version becomes latest and the dataset item (created on the first run) is linked.</summary>
    public void RecordVersion(DatasetVersion version, Guid contentItemId)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.TenantId != TenantId)
        {
            throw new DomainRuleException("cross_tenant_lineage", "The version belongs to another tenant.");
        }

        LatestVersionId = version.Id;
        ContentItemId = contentItemId;
    }

    private static ExtractDefinition Create(
        TenantId tenant, DataSourceKind kind, Guid? connectionId, string? table, Guid? fileId, Folder targetFolder, ItemName name, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(targetFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        if (targetFolder.TenantId != tenant)
        {
            throw new DomainRuleException("cross_tenant_folder", "The target folder belongs to another tenant.");
        }

        if (targetFolder.IsDeleted)
        {
            throw new DomainRuleException("folder_deleted", "Cannot extract into a deleted folder.");
        }

        return new ExtractDefinition
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            Name = name,
            SourceKind = kind,
            ConnectionProfileId = connectionId,
            SourceTable = table,
            StoredFileId = fileId,
            TargetFolderId = targetFolder.Id,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
        };
    }
}
