using Azure;
using Azure.Storage.Blobs;
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

/// <summary>Gives file connectors local, readable copies of uploaded files (which live in Blob Storage).</summary>
public interface ISourceFileAccessor
{
    Task<bool> ExistsAsync(TenantId tenant, Guid storedFileId, CancellationToken cancellationToken);

    /// <summary>Downloads the file into the work directory. Paths are built from ids only.</summary>
    Task<LocalFile> DownloadAsync(TenantId tenant, Guid storedFileId, string extension, CancellationToken cancellationToken);
}

/// <summary>Uploads a finished Parquet extract to its immutable blob path.</summary>
public interface IExtractUploader
{
    Task UploadAsync(TenantId tenant, Guid datasetVersionId, string localParquetPath, CancellationToken cancellationToken);
}

internal sealed class BlobSourceFileAccessor(BlobServiceClient blobs, IOptions<ConnectorOptions> options) : ISourceFileAccessor
{
    private BlobContainerClient Container => blobs.GetBlobContainerClient(options.Value.FilesContainer);

    public async Task<bool> ExistsAsync(TenantId tenant, Guid storedFileId, CancellationToken cancellationToken) =>
        (await Container.GetBlobClient(StoragePaths.File(tenant, storedFileId)).ExistsAsync(cancellationToken)).Value;

    public async Task<LocalFile> DownloadAsync(TenantId tenant, Guid storedFileId, string extension, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Value.WorkDirectory);
        var safeExtension = extension.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') ? extension : string.Empty;
        var path = Path.Combine(options.Value.WorkDirectory, $"{Guid.NewGuid():N}{safeExtension}");
        try
        {
            await Container.GetBlobClient(StoragePaths.File(tenant, storedFileId)).DownloadToAsync(path, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new ConnectorException("The uploaded file no longer exists.", ex);
        }

        return new LocalFile(path);
    }
}

internal sealed class BlobExtractUploader(BlobServiceClient blobs, IOptions<ConnectorOptions> options) : IExtractUploader
{
    private int _containerEnsured;

    public async Task UploadAsync(TenantId tenant, Guid datasetVersionId, string localParquetPath, CancellationToken cancellationToken)
    {
        var container = blobs.GetBlobContainerClient(options.Value.ExtractsContainer);
        if (Interlocked.CompareExchange(ref _containerEnsured, 1, 0) == 0)
        {
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }

        // Extracts are immutable: never overwrite (the QueryService cache relies on it).
        await container.GetBlobClient(StoragePaths.Extract(tenant, datasetVersionId)).UploadAsync(localParquetPath, cancellationToken);
    }
}
