using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InsightFlow.Query.Storage;

/// <summary>
/// <see cref="IExtractStore"/> on a directory shared with the extract pipeline (<see cref="LocalStorage"/>),
/// with a <see cref="LocalExtractCache"/> in front. Extracts are immutable, so a cached file cannot go stale.
/// DuckDB is only allowed to read the cache.
/// </summary>
public sealed partial class DirectoryExtractStore : IExtractStore
{
    private readonly string _root;
    private readonly LocalExtractCache _cache;
    private readonly ILogger<DirectoryExtractStore> _logger;

    public DirectoryExtractStore(IOptions<QueryEngineOptions> options, ILogger<DirectoryExtractStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = options.Value.StorageRoot;
        _cache = new LocalExtractCache(options.Value.CacheDirectory, options.Value.MaxCacheBytes);
        _logger = logger;
    }

    public string LocalRoot => _cache.Root;

    public Task<string> GetLocalPathAsync(DatasetVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        return _cache.GetOrAddAsync(version.TenantId, version.Id, async (temp, ct) =>
        {
            var source = LocalStorage.Resolve(_root, StoragePaths.Extract(version.TenantId, version.Id));
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true);
            await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, useAsync: true);
            await input.CopyToAsync(output, ct);
            QueryTelemetry.ExtractDownloads.Add(1);
            LogCopied(_logger, version.Id, new FileInfo(temp).Length);
        }, cancellationToken);
    }

    public async Task<Uri> SaveAsync(TenantId tenant, Guid datasetVersionId, Stream parquet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parquet);
        var relative = StoragePaths.Extract(tenant, datasetVersionId);
        if (LocalStorage.Exists(_root, relative))
        {
            throw new InvalidOperationException($"Extract {datasetVersionId} already exists; extracts are immutable.");
        }

        var local = await _cache.AddAsync(tenant, datasetVersionId, parquet, cancellationToken);
        try
        {
            await LocalStorage.CopyNewAsync(_root, relative, local, cancellationToken);
        }
        catch (IOException ex) when (LocalStorage.Exists(_root, relative))
        {
            throw new InvalidOperationException($"Extract {datasetVersionId} already exists; extracts are immutable.", ex);
        }

        return new Uri(LocalStorage.Resolve(_root, relative));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Copied extract {DatasetVersionId} ({Bytes} bytes) into the local cache")]
    private static partial void LogCopied(ILogger logger, Guid datasetVersionId, long bytes);
}
