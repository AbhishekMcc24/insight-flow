using Azure;
using Azure.Storage.Blobs;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InsightFlow.Query.Storage;

/// <summary>
/// <see cref="IExtractStore"/> on Azure Blob Storage (Azurite locally) with a <see cref="LocalExtractCache"/> in front.
/// Extracts are immutable: uploads never overwrite, so a cached file can never be stale.
/// </summary>
public sealed partial class BlobExtractStore : IExtractStore
{
    private readonly BlobContainerClient _container;
    private readonly LocalExtractCache _cache;
    private readonly ILogger<BlobExtractStore> _logger;
    private int _containerEnsured;

    public BlobExtractStore(BlobServiceClient blobs, IOptions<QueryEngineOptions> options, ILogger<BlobExtractStore> logger)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(options);
        _container = blobs.GetBlobContainerClient(options.Value.ExtractsContainer);
        _cache = new LocalExtractCache(options.Value.CacheDirectory, options.Value.MaxCacheBytes);
        _logger = logger;
    }

    public string LocalRoot => _cache.Root;

    public Task<string> GetLocalPathAsync(DatasetVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        return _cache.GetOrAddAsync(version.TenantId, version.Id, async (temp, ct) =>
        {
            var blob = _container.GetBlobClient(StoragePaths.Extract(version.TenantId, version.Id));
            await blob.DownloadToAsync(temp, ct);
            QueryTelemetry.ExtractDownloads.Add(1);
            LogDownloaded(_logger, version.Id, new FileInfo(temp).Length);
        }, cancellationToken);
    }

    public async Task<Uri> SaveAsync(TenantId tenant, Guid datasetVersionId, Stream parquet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parquet);
        await EnsureContainerAsync(cancellationToken);

        // Write to the cache first, then upload from that file: the first query after an extract needs no download.
        var local = await _cache.AddAsync(tenant, datasetVersionId, parquet, cancellationToken);
        var blob = _container.GetBlobClient(StoragePaths.Extract(tenant, datasetVersionId));
        try
        {
            await using var file = File.OpenRead(local);
            await blob.UploadAsync(file, overwrite: false, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new InvalidOperationException($"Extract {datasetVersionId} already exists; extracts are immutable.", ex);
        }

        return blob.Uri;
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _containerEnsured, 1, 0) == 0)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloaded extract {DatasetVersionId} ({Bytes} bytes) into the local cache")]
    private static partial void LogDownloaded(ILogger logger, Guid datasetVersionId, long bytes);
}
