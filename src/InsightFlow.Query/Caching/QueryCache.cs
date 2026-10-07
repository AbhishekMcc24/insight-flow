using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using InsightFlow.Contracts;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Viz;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InsightFlow.Query.Caching;

/// <summary>
/// Cache key of a chart result: SHA-256 over tenant, dataset version, dialect, semantic-model fingerprint and the
/// canonical spec JSON (filters sorted, so logically equal specs share an entry). Specs with relative date filters
/// also include the current UTC hour, so "last 7 days" never serves yesterday's window for long.
/// </summary>
public readonly record struct QueryCacheKey(string Value)
{
    private const string Version = "v1";

    public static QueryCacheKey Create(TenantId tenant, VizSpec spec, SemanticModel model, string dialect, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(model);

        var timeBucket = spec.Filters.Any(f => f is RelativeDateFilter)
            ? now.UtcDateTime.ToString("yyyy-MM-ddTHH", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

        var material = string.Join('|', Version, tenant, spec.DatasetVersionId, dialect, ModelFingerprint(model), CanonicalJson(spec), timeBucket);
        return new QueryCacheKey($"insightflow:query:{Version}:{Hash(material)}");
    }

    /// <summary>Spec JSON with filters in a stable order (filters are AND-ed, so order carries no meaning).</summary>
    public static string CanonicalJson(VizSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var sorted = spec.Filters
            .Select(f => (Filter: f, Json: JsonSerializer.Serialize(f, VizJsonContext.Default.FilterSpec)))
            .OrderBy(x => x.Json, StringComparer.Ordinal)
            .Select(x => x.Filter)
            .ToList();
        return VizSpecJson.Serialize(spec with { Filters = sorted });
    }

    /// <summary>Changes whenever the model's tables, relationships or measures change (they alter the compiled SQL).</summary>
    public static string ModelFingerprint(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Hash(JsonSerializer.Serialize(model, QueryJsonContext.Default.SemanticModel));
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    public override string ToString() => Value;
}

/// <summary>Result cache for chart queries (D11). A cache failure must never fail a query: errors are logged and treated as misses.</summary>
public interface IQueryCache
{
    Task<QueryResult?> GetAsync(QueryCacheKey key, CancellationToken cancellationToken);

    Task SetAsync(QueryCacheKey key, QueryResult result, CancellationToken cancellationToken);
}

/// <summary><see cref="IQueryCache"/> over <see cref="IDistributedCache"/> (Redis in every environment).</summary>
public sealed partial class DistributedQueryCache(
    IDistributedCache cache,
    IOptions<QueryEngineOptions> options,
    ILogger<DistributedQueryCache> logger) : IQueryCache
{
    public async Task<QueryResult?> GetAsync(QueryCacheKey key, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await cache.GetAsync(key.Value, cancellationToken);
            return bytes is null ? null : JsonSerializer.Deserialize(bytes, ContractsJsonContext.Default.QueryResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCacheError(logger, "read", ex);
            return null;
        }
    }

    public async Task SetAsync(QueryCacheKey key, QueryResult result, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, ContractsJsonContext.Default.QueryResult);
            await cache.SetAsync(
                key.Value,
                bytes,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Value.CacheTtl },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCacheError(logger, "write", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query cache {Operation} failed; continuing without cache")]
    private static partial void LogCacheError(ILogger logger, string operation, Exception exception);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SemanticModel))]
internal sealed partial class QueryJsonContext : JsonSerializerContext;
