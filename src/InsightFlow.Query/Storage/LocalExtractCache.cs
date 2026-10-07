using System.Collections.Concurrent;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Query.Storage;

/// <summary>
/// Bounded on-disk LRU cache of Parquet extracts. Paths are built from ids only
/// (<c>{root}/{tenantId}/{datasetVersionId}.parquet</c>). Concurrent requests for the same missing file share one
/// download. When the total size exceeds the budget the least recently used files are deleted; files currently open
/// (e.g. by a running query on Windows) are skipped and retried on the next eviction pass.
/// </summary>
public sealed class LocalExtractCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inflight = new(StringComparer.Ordinal);
    private readonly Lock _evictionLock = new();
    private readonly long _maxBytes;

    public LocalExtractCache(string root, long maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        Root = Path.GetFullPath(root);
        _maxBytes = maxBytes;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathFor(TenantId tenant, Guid datasetVersionId) =>
        Path.Combine(Root, tenant.Value.ToString("N"), $"{datasetVersionId:N}.parquet");

    /// <summary>Returns the cached file (touching its access time) or fills it via <paramref name="fill"/>, which writes to the given temp path.</summary>
    public async Task<string> GetOrAddAsync(
        TenantId tenant, Guid datasetVersionId, Func<string, CancellationToken, Task> fill, CancellationToken cancellationToken)
    {
        var path = PathFor(tenant, datasetVersionId);
        if (File.Exists(path))
        {
            Touch(path);
            return path;
        }

        // The shared download is not tied to the first caller's token; each waiter honours its own via WaitAsync.
        var lazy = _inflight.GetOrAdd(path, p => new Lazy<Task<string>>(() => FillAsync(p, fill, CancellationToken.None)));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(path, lazy));
        }
    }

    /// <summary>Copies a stream into the cache (used when a new extract is saved, so the first query needs no download).</summary>
    public Task<string> AddAsync(TenantId tenant, Guid datasetVersionId, Stream content, CancellationToken cancellationToken) =>
        FillAsync(
            PathFor(tenant, datasetVersionId),
            async (temp, ct) =>
            {
                await using var file = File.Create(temp);
                await content.CopyToAsync(file, ct);
            },
            cancellationToken);

    /// <summary>Deletes least-recently-used files until the cache is within budget.</summary>
    public void Evict()
    {
        lock (_evictionLock)
        {
            var files = new DirectoryInfo(Root).EnumerateFiles("*.parquet", SearchOption.AllDirectories)
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);

            foreach (var file in files)
            {
                if (total <= _maxBytes)
                {
                    break;
                }

                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                }
                catch (IOException)
                {
                    // In use by a running query; try again on the next pass.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same as above on some platforms.
                }
            }
        }
    }

    private async Task<string> FillAsync(string path, Func<string, CancellationToken, Task> fill, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await fill(temp, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        Touch(path);
        Evict();
        return path;
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // Best effort: access time only drives eviction order.
        }
    }
}
