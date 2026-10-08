using InsightFlow.Connectors;
using InsightFlow.Connectors.Stubs;
using InsightFlow.Contracts.Workspace;
using InsightFlow.Domain;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Workspace;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InsightFlow.Api.Workspace;

/// <summary>
/// Application logic of the workspace explorer: loads tree facts from the store, applies the domain rules
/// (<see cref="FolderTreeRules"/>, <see cref="ItemName"/>, <see cref="NameConflicts"/>) and the permission evaluator,
/// and writes atomically. Folders the caller may not read are reported as "not found" so their existence never leaks.
/// TODO(dev2): trash/restore, per-folder sharing, search, bulk zip download, file versioning.
/// </summary>
public sealed partial class WorkspaceService(
    InsightFlowDbContext db,
    ITenantContext caller,
    IContentPermissionEvaluator permissions,
    IFileStore files,
    IUploadScanner scanner,
    IConnectorRegistry connectors,
    IOptions<UploadOptions> uploadOptions,
    TimeProvider clock,
    ILogger<WorkspaceService> logger)
{
    private TenantId Tenant => new(caller.TenantId ?? throw new InvalidOperationException("No tenant in scope."));

    private string UserId => caller.UserId ?? throw new InvalidOperationException("No user in scope.");

    private WorkspacePrincipal Principal => new(Tenant, UserId, HighestRole());

    // ---------------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------------

    /// <summary>The caller's "My Workspace" and the tenant's "Shared" roots, created on first use.</summary>
    public async Task<WorkspaceRootsResponse> GetRootsAsync(CancellationToken cancellationToken)
    {
        await EnsureTenantProvisionedAsync(cancellationToken);
        var personal = await EnsurePersonalRootAsync(cancellationToken);
        var shared = await db.Folders.SingleAsync(f => f.ParentId == null && f.Scope == FolderScope.Shared, cancellationToken);
        return new WorkspaceRootsResponse(ToDto(personal), ToDto(shared));
    }

    public async Task<FolderContentsResponse> GetFolderContentsAsync(Guid folderId, CancellationToken cancellationToken)
    {
        var folder = await LoadReadableFolderAsync(folderId, cancellationToken);
        var chain = await db.GetFolderAndAncestorIdsAsync(Tenant, folder.Id, cancellationToken);
        var ancestors = await db.Folders.Where(f => chain.Contains(f.Id)).ToDictionaryAsync(f => f.Id, cancellationToken);
        var path = chain.Reverse().Where(ancestors.ContainsKey).Select(id => ToDto(ancestors[id])).ToList();

        var children = await db.Folders.Where(f => f.ParentId == folder.Id).OrderBy(f => f.Name).ToListAsync(cancellationToken);
        var items = await db.ContentItems.Where(i => i.FolderId == folder.Id).OrderBy(i => i.Name).ToListAsync(cancellationToken);
        var fileIds = items.Where(i => i.Kind == ContentKind.File).Select(i => i.TargetId).ToList();
        var storedFiles = await db.StoredFiles.Where(f => fileIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, cancellationToken);
        var canWrite = permissions.CanWrite(folder, Principal);

        return new FolderContentsResponse(
            ToDto(folder),
            path,
            children.Select(ToDto).ToList(),
            items.Select(i => ToDto(i, storedFiles.GetValueOrDefault(i.TargetId), canWrite)).ToList());
    }

    /// <summary>
    /// Every dataset item in a folder the caller can read, newest first (dataset pickers).
    /// TODO(dev2): paging and search once tenants have many datasets.
    /// </summary>
    public async Task<IReadOnlyList<DatasetSummaryDto>> ListDatasetsAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from item in db.ContentItems
                join folder in db.Folders on item.FolderId equals folder.Id
                where item.Kind == ContentKind.Dataset
                orderby item.CreatedAt descending
                select new { item, folder })
            .Take(500)
            .ToListAsync(cancellationToken);

        return rows
            .Where(r => permissions.CanRead(r.folder, Principal))
            .Select(r => new DatasetSummaryDto(r.item.Id, r.item.TargetId, r.item.Name.Value, r.folder.Id, r.folder.Name.Value, r.item.CreatedAt))
            .ToList();
    }

    public async Task<(Stream Content, string FileName, string ContentType)> OpenFileAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await LoadItemAsync(itemId, write: false, cancellationToken);
        if (item.Kind != ContentKind.File)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "not_a_file", "Only files can be downloaded.");
        }

        var stored = await db.StoredFiles.FindAsync([item.TargetId], cancellationToken) ?? throw ApiProblemException.NotFound("The file");
        var stream = await files.OpenReadAsync(Tenant, stored.Id, cancellationToken);
        return (stream, item.Name.Value, stored.ContentType);
    }

    // ---------------------------------------------------------------------------------------------
    // Tree changes
    // ---------------------------------------------------------------------------------------------

    public async Task<FolderDto> CreateFolderAsync(CreateFolderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parent = await LoadWritableFolderAsync(request.ParentId, cancellationToken);
        var name = ItemName.Create(request.Name);
        await EnsureFolderNameFreeAsync(parent.Id, name, cancellationToken);

        var depth = (await db.GetFolderAndAncestorIdsAsync(Tenant, parent.Id, cancellationToken)).Count - 1;
        var folder = Folder.CreateChild(parent, depth, name, UserId, clock.GetUtcNow());
        db.Folders.Add(folder);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(folder);
    }

    public async Task<FolderDto> RenameFolderAsync(Guid folderId, string newName, CancellationToken cancellationToken)
    {
        var folder = await LoadWritableFolderAsync(folderId, cancellationToken);
        var name = ItemName.Create(newName);
        if (!name.Collides(folder.Name) && folder.ParentId is { } parentId)
        {
            await EnsureFolderNameFreeAsync(parentId, name, cancellationToken);
        }

        folder.Rename(name);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(folder);
    }

    public async Task<ContentItemDto> RenameItemAsync(Guid itemId, string newName, CancellationToken cancellationToken)
    {
        var item = await LoadItemAsync(itemId, write: true, cancellationToken);
        var name = ItemName.Create(newName);
        if (!name.Collides(item.Name))
        {
            await EnsureItemNameFreeAsync(item.FolderId, name, cancellationToken);
        }

        item.Rename(name);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item, null, canWrite: true);
    }

    /// <summary>Moves folders and items in one transaction; any rule violation aborts the whole move.</summary>
    public async Task MoveAsync(MoveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = await LoadWritableFolderAsync(request.TargetFolderId, cancellationToken);
        var targetChain = await db.GetFolderAndAncestorIdsAsync(Tenant, target.Id, cancellationToken);
        var targetDepth = targetChain.Count - 1;

        foreach (var folderId in request.FolderIds.Distinct())
        {
            var folder = await LoadWritableFolderAsync(folderId, cancellationToken);
            if (folder.ParentId == target.Id)
            {
                continue;
            }

            await EnsureFolderNameFreeAsync(target.Id, folder.Name, cancellationToken);
            var height = await db.GetSubtreeHeightAsync(Tenant, folder.Id, cancellationToken);
            var scopeChanges = folder.Scope != target.Scope || folder.OwnerUserId != target.OwnerUserId;
            folder.MoveTo(target, targetChain, targetDepth, height);

            if (scopeChanges)
            {
                var descendantIds = await db.GetDescendantFolderIdsAsync(Tenant, folder.Id, cancellationToken);
                foreach (var descendant in await db.Folders.Where(f => descendantIds.Contains(f.Id)).ToListAsync(cancellationToken))
                {
                    descendant.ApplyScopeFrom(target);
                }
            }
        }

        foreach (var itemId in request.ItemIds.Distinct())
        {
            var item = await LoadItemAsync(itemId, write: true, cancellationToken);
            if (item.FolderId == target.Id)
            {
                continue;
            }

            await EnsureItemNameFreeAsync(target.Id, item.Name, cancellationToken);
            item.MoveTo(target);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        var folder = await LoadWritableFolderAsync(folderId, cancellationToken);
        folder.SoftDelete(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await LoadItemAsync(itemId, write: true, cancellationToken);
        item.SoftDelete(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------
    // Uploads
    // ---------------------------------------------------------------------------------------------

    /// <summary>Validates the drop target once per request; folders created for nested paths are cached in the session.</summary>
    public async Task<UploadSession> BeginUploadAsync(Guid folderId, CancellationToken cancellationToken)
    {
        var root = await LoadWritableFolderAsync(folderId, cancellationToken);
        var depth = (await db.GetFolderAndAncestorIdsAsync(Tenant, root.Id, cancellationToken)).Count - 1;
        return new UploadSession(root, depth);
    }

    /// <summary>
    /// Stores one file at <paramref name="relativePath"/> below the session's folder, creating missing sub-folders. The
    /// body streams to Blob Storage while being hashed and size-checked; name clashes become "name (2).ext".
    /// </summary>
    public async Task<UploadFileResult> UploadFileAsync(
        UploadSession session, string relativePath, Stream content, string? contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(content);
        var options = uploadOptions.Value;
        if (++session.FileCount > options.MaxFilesPerRequest)
        {
            return new UploadFileResult(relativePath, false, Error: $"At most {options.MaxFilesPerRequest} files per upload.");
        }

        UploadPath path;
        try
        {
            path = UploadPath.Parse(relativePath);
        }
        catch (DomainRuleException ex)
        {
            return new UploadFileResult(relativePath, false, Error: ex.Message);
        }

        if (!options.AllowedExtensions.Contains(path.FileName.Extension, StringComparer.OrdinalIgnoreCase))
        {
            return new UploadFileResult(relativePath, false, Error: $"Files of type '{path.FileName.Extension}' are not allowed.");
        }

        var folder = await EnsureFolderPathAsync(session, path.Folders, cancellationToken);
        var existing = await db.ContentItems.Where(i => i.FolderId == folder.Id).Select(i => i.Name).ToListAsync(cancellationToken);
        var name = NameConflicts.NextAvailable(path.FileName, existing);

        var storedFileId = StoredFile.NewId();
        var type = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        await using var hashing = new HashingStream(content, options.MaxFileBytes);
        try
        {
            await files.UploadAsync(Tenant, storedFileId, hashing, type, cancellationToken);
        }
        catch (UploadTooLargeException ex)
        {
            await files.DeleteAsync(Tenant, storedFileId, cancellationToken);
            return new UploadFileResult(relativePath, false, Error: ex.Message);
        }

        var sha = hashing.GetHashHex();
        if (await scanner.ScanAsync(storedFileId, sha, cancellationToken) is { } rejection)
        {
            await files.DeleteAsync(Tenant, storedFileId, cancellationToken);
            return new UploadFileResult(relativePath, false, Error: rejection);
        }

        var now = clock.GetUtcNow();
        var stored = StoredFile.Create(storedFileId, Tenant, path.FileName, type, hashing.BytesRead, sha, UserId, now);
        var item = ContentItem.Create(folder, name, ContentKind.File, stored.Id, UserId, now);
        db.StoredFiles.Add(stored);
        db.ContentItems.Add(item);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent upload took the name between our check and insert: report it rather than guess again.
            db.ChangeTracker.Clear();
            await files.DeleteAsync(Tenant, storedFileId, cancellationToken);
            return new UploadFileResult(relativePath, false, Error: "A file with the same name was uploaded at the same time; please retry.");
        }

        LogUploaded(logger, stored.Id, stored.SizeBytes);
        return new UploadFileResult(relativePath, true, item.Id, item.Name.Value, folder.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // Datasets
    // ---------------------------------------------------------------------------------------------

    /// <summary>Queues "Create dataset" for an uploaded CSV/Excel/Parquet file; the dataset item appears next to the file.</summary>
    public async Task<ExtractQueuedResponse> QueueDatasetFromFileAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await LoadItemAsync(itemId, write: true, cancellationToken);
        if (item.Kind != ContentKind.File)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "not_a_file", "Only uploaded files can become datasets.");
        }

        var stored = await db.StoredFiles.FindAsync([item.TargetId], cancellationToken) ?? throw ApiProblemException.NotFound("The file");
        var format = StoredFile.DetectTabularFormat(item.Name)
            ?? throw new ApiProblemException(StatusCodes.Status400BadRequest, "unsupported_format", "Only CSV, Excel and Parquet files can become datasets.");
        var folder = await db.Folders.SingleAsync(f => f.Id == item.FolderId, cancellationToken);
        var definition = ExtractDefinition.ForFile(stored, format, folder, ItemName.Create(item.Name.Stem), UserId, clock.GetUtcNow());
        EnsureConnectorImplemented(definition.SourceKind);
        return await EnqueueAsync(definition, cancellationToken);
    }

    /// <summary>Queues an extract of a table from a saved connection into <paramref name="folderId"/>.</summary>
    public async Task<ExtractQueuedResponse> QueueExtractFromConnectionAsync(
        ConnectionProfile connection, string table, Guid folderId, string? name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var folder = await LoadWritableFolderAsync(folderId, cancellationToken);
        EnsureConnectorImplemented(connection.Kind);
        var displayName = ItemName.Create(string.IsNullOrWhiteSpace(name) ? table.Split('.')[^1] : name);
        var definition = ExtractDefinition.ForConnection(connection, table, folder, displayName, UserId, clock.GetUtcNow());
        return await EnqueueAsync(definition, cancellationToken);
    }

    public async Task<ExtractRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.ExtractRuns.FindAsync([runId], cancellationToken) ?? throw ApiProblemException.NotFound("The extract run");
        var definition = await db.ExtractDefinitions.FindAsync([run.DefinitionId], cancellationToken);
        return new ExtractRunDto(run.Id, run.DefinitionId, run.Status.ToString(), run.RequestedAt, run.CompletedAt, run.ResultVersionId,
            definition?.ContentItemId, run.RowCount, run.Error);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private async Task<ExtractQueuedResponse> EnqueueAsync(ExtractDefinition definition, CancellationToken cancellationToken)
    {
        var run = ExtractRun.Enqueue(definition, UserId, clock.GetUtcNow());
        db.ExtractDefinitions.Add(definition);
        db.ExtractRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return new ExtractQueuedResponse(run.Id, definition.Id);
    }

    private void EnsureConnectorImplemented(DataSourceKind kind)
    {
        if (connectors.Resolve(kind) is NotImplementedConnector)
        {
            throw new ApiProblemException(StatusCodes.Status501NotImplemented, "connector_not_implemented", $"{kind} sources are not supported yet.");
        }
    }

    private async Task<Folder> EnsureFolderPathAsync(UploadSession session, IReadOnlyList<ItemName> segments, CancellationToken cancellationToken)
    {
        var current = session.Root;
        var depth = session.RootDepth;
        foreach (var segment in segments)
        {
            depth++;
            var cacheKey = (current.Id, segment.Key);
            if (session.Folders.TryGetValue(cacheKey, out var cached))
            {
                current = cached;
                continue;
            }

            var key = segment.Key;
            var parentId = current.Id;
            var existing = await db.Folders.FirstOrDefaultAsync(
                f => f.ParentId == parentId && EF.Property<string>(f, "NameKey") == key, cancellationToken);
            if (existing is null)
            {
                existing = Folder.CreateChild(current, depth - 1, segment, UserId, clock.GetUtcNow());
                db.Folders.Add(existing);
                await db.SaveChangesAsync(cancellationToken);
            }

            session.Folders[cacheKey] = existing;
            current = existing;
        }

        return current;
    }

    private async Task EnsureTenantProvisionedAsync(CancellationToken cancellationToken)
    {
        // TODO(dev2): replace lazy provisioning with explicit tenant onboarding (admin flow) once it exists.
        if (await db.Tenants.AnyAsync(cancellationToken))
        {
            if (await db.Folders.AnyAsync(f => f.ParentId == null && f.Scope == FolderScope.Shared, cancellationToken))
            {
                return;
            }
        }
        else
        {
            db.Tenants.Add(Domain.Tenancy.Tenant.Create(Tenant, $"Tenant {Tenant}", clock.GetUtcNow()));
        }

        db.Folders.Add(Folder.CreateSharedRoot(Tenant, clock.GetUtcNow()));
        await SaveIgnoringRaceAsync(cancellationToken);
    }

    private async Task<Folder> EnsurePersonalRootAsync(CancellationToken cancellationToken)
    {
        var userId = UserId;
        var root = await db.Folders.SingleOrDefaultAsync(
            f => f.ParentId == null && f.Scope == FolderScope.Personal && f.OwnerUserId == userId, cancellationToken);
        if (root is not null)
        {
            return root;
        }

        db.Folders.Add(Folder.CreatePersonalRoot(Tenant, userId, clock.GetUtcNow()));
        await SaveIgnoringRaceAsync(cancellationToken);
        return await db.Folders.SingleAsync(f => f.ParentId == null && f.Scope == FolderScope.Personal && f.OwnerUserId == userId, cancellationToken);
    }

    /// <summary>Two first requests may provision concurrently; the unique indexes keep one row and the loser re-reads.</summary>
    private async Task SaveIgnoringRaceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<Folder> LoadReadableFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        var folder = await db.Folders.FindAsync([folderId], cancellationToken);
        return folder is not null && permissions.CanRead(folder, Principal) ? folder : throw ApiProblemException.NotFound("The folder");
    }

    private async Task<Folder> LoadWritableFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        var folder = await LoadReadableFolderAsync(folderId, cancellationToken);
        return permissions.CanWrite(folder, Principal)
            ? folder
            : throw ApiProblemException.Forbidden("You do not have permission to change this folder.");
    }

    private async Task<ContentItem> LoadItemAsync(Guid itemId, bool write, CancellationToken cancellationToken)
    {
        var item = await db.ContentItems.FindAsync([itemId], cancellationToken) ?? throw ApiProblemException.NotFound("The item");
        if (write)
        {
            await LoadWritableFolderAsync(item.FolderId, cancellationToken);
        }
        else
        {
            await LoadReadableFolderAsync(item.FolderId, cancellationToken);
        }

        return item;
    }

    private async Task EnsureFolderNameFreeAsync(Guid parentId, ItemName name, CancellationToken cancellationToken)
    {
        var key = name.Key;
        if (await db.Folders.AnyAsync(f => f.ParentId == parentId && EF.Property<string>(f, "NameKey") == key, cancellationToken))
        {
            throw ApiProblemException.Conflict($"A folder named '{name}' already exists here.");
        }
    }

    private async Task EnsureItemNameFreeAsync(Guid folderId, ItemName name, CancellationToken cancellationToken)
    {
        var key = name.Key;
        if (await db.ContentItems.AnyAsync(i => i.FolderId == folderId && EF.Property<string>(i, "NameKey") == key, cancellationToken))
        {
            throw ApiProblemException.Conflict($"An item named '{name}' already exists here.");
        }
    }

    private TenantRole HighestRole()
    {
        var roles = caller.Roles;
        return roles.Contains(InsightFlowRoles.TenantAdmin) ? TenantRole.TenantAdmin
            : roles.Contains(InsightFlowRoles.Creator) ? TenantRole.Creator
            : roles.Contains(InsightFlowRoles.Explorer) ? TenantRole.Explorer
            : TenantRole.Viewer;
    }

    private FolderDto ToDto(Folder folder) =>
        new(folder.Id, folder.ParentId, folder.Name.Value, folder.Scope.ToString(), permissions.CanWrite(folder, Principal));

    private static ContentItemDto ToDto(ContentItem item, StoredFile? file, bool canWrite) =>
        new(item.Id, item.FolderId, item.Name.Value, item.Kind.ToString(), item.TargetId, item.CreatedAt,
            file?.SizeBytes, file?.ContentType,
            CanCreateDataset: canWrite && item.Kind == ContentKind.File && StoredFile.DetectTabularFormat(item.Name) is not null);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored uploaded file {StoredFileId} ({Bytes} bytes)")]
    private static partial void LogUploaded(ILogger logger, Guid storedFileId, long bytes);
}

/// <summary>Per-request upload state: the drop target, its depth and the sub-folders created so far.</summary>
public sealed class UploadSession(Folder root, int rootDepth)
{
    public Folder Root { get; } = root;

    public int RootDepth { get; } = rootDepth;

    public int FileCount { get; set; }

    internal Dictionary<(Guid ParentId, string Key), Folder> Folders { get; } = [];
}
