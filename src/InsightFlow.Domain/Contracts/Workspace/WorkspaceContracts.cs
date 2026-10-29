namespace InsightFlow.Domain.Contracts.Workspace;

/// <summary>A folder in the workspace explorer. <see cref="Scope"/> is <c>Personal</c> or <c>Shared</c>.</summary>
public sealed record FolderDto(Guid Id, Guid? ParentId, string Name, string Scope, bool CanWrite);

/// <summary>
/// An entry in a folder. <see cref="Kind"/> is <c>File</c>, <c>Dataset</c>, <c>DataThread</c>, <c>Workbook</c> or
/// <c>Dashboard</c>; <see cref="TargetId"/> is the stored file, dataset version or thread it points to.
/// </summary>
public sealed record ContentItemDto(
    Guid Id,
    Guid FolderId,
    string Name,
    string Kind,
    Guid TargetId,
    DateTimeOffset CreatedAt,
    long? SizeBytes = null,
    string? ContentType = null,
    bool CanCreateDataset = false);

/// <summary>The two roots every user sees.</summary>
public sealed record WorkspaceRootsResponse(FolderDto MyWorkspace, FolderDto Shared);

/// <summary>A folder's direct children plus its breadcrumb (root first).</summary>
public sealed record FolderContentsResponse(
    FolderDto Folder,
    IReadOnlyList<FolderDto> Path,
    IReadOnlyList<FolderDto> Folders,
    IReadOnlyList<ContentItemDto> Items);

public sealed record CreateFolderRequest(Guid ParentId, string Name);

public sealed record RenameRequest(string Name);

/// <summary>Moves folders and items into <see cref="TargetFolderId"/> atomically (all or nothing).</summary>
public sealed record MoveRequest(IReadOnlyList<Guid> FolderIds, IReadOnlyList<Guid> ItemIds, Guid TargetFolderId);

/// <summary>Per-file outcome of a multipart upload; partial success is normal (one bad file does not fail the rest).</summary>
public sealed record UploadFileResult(string Path, bool Success, Guid? ItemId = null, string? Name = null, Guid? FolderId = null, string? Error = null);

public sealed record UploadResponse(IReadOnlyList<UploadFileResult> Files);

/// <summary>Returned (202) when a dataset extract has been queued.</summary>
public sealed record ExtractQueuedResponse(Guid RunId, Guid DefinitionId);

/// <summary>Status of an extract run. <see cref="Status"/>: <c>Pending</c>, <c>Running</c>, <c>Succeeded</c>, <c>Failed</c>.</summary>
public sealed record ExtractRunDto(
    Guid Id,
    Guid DefinitionId,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    Guid? DatasetVersionId,
    Guid? ContentItemId,
    long? RowCount,
    string? Error);

/// <summary>A dataset the caller can read (for pickers): the workspace item and the dataset version it points to.</summary>
public sealed record DatasetSummaryDto(Guid ItemId, Guid DatasetVersionId, string Name, Guid FolderId, string FolderName, DateTimeOffset CreatedAt);

/// <summary>Documents uploaded on one calendar day (UTC).</summary>
public sealed record DailyCountDto(DateOnly Day, int Count);

/// <summary>Documents of one file type (lower-case extension without the dot, or "other").</summary>
public sealed record FileTypeStatDto(string Type, int Count, long Bytes);

/// <summary>Documents in one folder.</summary>
public sealed record FolderStatDto(string Folder, int Count);

/// <summary>
/// Headline numbers and chart series for the Home dashboard, limited to what the caller can read.
/// <see cref="UploadsByDay"/> covers the last 30 days with zero-filled gaps.
/// </summary>
public sealed record WorkspaceStatsDto(
    int TotalDocuments,
    int DocumentsLast7Days,
    int TotalDatasets,
    long TotalBytes,
    long TotalRowsProcessed,
    int ExtractsSucceeded,
    int ExtractsFailed,
    int ExtractsInProgress,
    IReadOnlyList<DailyCountDto> UploadsByDay,
    IReadOnlyList<FileTypeStatDto> ByFileType,
    IReadOnlyList<FolderStatDto> ByFolder);
