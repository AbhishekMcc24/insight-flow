using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using Microsoft.Extensions.Options;

namespace InsightFlow.Connectors.Extraction;

/// <summary>A downloaded copy of an uploaded file; deleted on dispose.</summary>
public sealed class LocalFile(string path) : IAsyncDisposable
{
    public string Path { get; } = path;

    public ValueTask DisposeAsync()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // Temp file in the work directory; cleaned up on the next run.
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Gives file connectors local, readable copies of uploaded files (which live under <see cref="LocalStorage"/>).</summary>
public interface ISourceFileAccessor
{
    Task<bool> ExistsAsync(TenantId tenant, Guid storedFileId, CancellationToken cancellationToken);

    /// <summary>Downloads the file into the work directory. Paths are built from ids only.</summary>
    Task<LocalFile> DownloadAsync(TenantId tenant, Guid storedFileId, string extension, CancellationToken cancellationToken);
}

/// <summary>Copies a finished Parquet extract to its immutable path in the shared storage directory.</summary>
public interface IExtractUploader
{
    Task UploadAsync(TenantId tenant, Guid datasetVersionId, string localParquetPath, CancellationToken cancellationToken);
}

internal sealed class DirectorySourceFileAccessor(IOptions<ConnectorOptions> options) : ISourceFileAccessor
{
    public Task<bool> ExistsAsync(TenantId tenant, Guid storedFileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LocalStorage.Exists(options.Value.StorageRoot, StoragePaths.File(tenant, storedFileId)));
    }

    public async Task<LocalFile> DownloadAsync(TenantId tenant, Guid storedFileId, string extension, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Value.WorkDirectory);
        var safeExtension = extension.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') ? extension : string.Empty;
        var path = Path.Combine(options.Value.WorkDirectory, $"{Guid.NewGuid():N}{safeExtension}");
        var source = LocalStorage.Resolve(options.Value.StorageRoot, StoragePaths.File(tenant, storedFileId));
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            throw new ConnectorException("The uploaded file no longer exists.", ex);
        }

        return new LocalFile(path);
    }
}

internal sealed class DirectoryExtractUploader(IOptions<ConnectorOptions> options) : IExtractUploader
{
    public async Task UploadAsync(TenantId tenant, Guid datasetVersionId, string localParquetPath, CancellationToken cancellationToken)
    {
        var relative = StoragePaths.Extract(tenant, datasetVersionId);
        try
        {
            // Extracts are immutable: never overwrite (the QueryService cache relies on it).
            await LocalStorage.CopyNewAsync(options.Value.StorageRoot, relative, localParquetPath, cancellationToken);
        }
        catch (IOException ex) when (LocalStorage.Exists(options.Value.StorageRoot, relative))
        {
            throw new InvalidOperationException($"Extract {datasetVersionId} already exists; extracts are immutable.", ex);
        }
    }
}
