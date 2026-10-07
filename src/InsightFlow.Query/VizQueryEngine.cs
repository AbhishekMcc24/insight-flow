using System.Diagnostics;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Viz;
using InsightFlow.Query.Caching;
using InsightFlow.Query.Compilation;
using InsightFlow.Query.Dialects;
using InsightFlow.Query.Execution;

namespace InsightFlow.Query;

/// <summary>Result of running a chart query, including the SQL shown to users next to every chart.</summary>
public sealed record VizQueryOutcome(QueryResult Result, CompiledQuery Query, bool FromCache, TimeSpan Duration);

/// <summary>
/// The query pipeline used by QueryService and by the agents (derived-field flow): compile → cache lookup → execute
/// on DuckDB → cache store. Callers pass already tenant-checked dataset versions and model; this type never loads
/// metadata itself, which keeps it free of persistence and easy to test.
/// </summary>
public interface IVizQueryEngine
{
    /// <exception cref="VizSpecValidationException">The spec is invalid for the model.</exception>
    Task<VizQueryOutcome> RunAsync(
        TenantId tenant, VizSpec spec, SemanticModel model, IReadOnlyList<DatasetVersion> versions, CancellationToken cancellationToken);

    /// <summary>First <paramref name="limit"/> rows of a dataset version (no cache: previews are cheap and rarely repeated).</summary>
    Task<QueryResult> PreviewAsync(DatasetVersion version, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class VizQueryEngine(
    ISqlCompiler compiler,
    IQueryExecutor executor,
    IQueryCache cache,
    TimeProvider clock) : IVizQueryEngine
{
    private readonly DuckDbDialect _dialect = DuckDbDialect.Instance;

    public async Task<VizQueryOutcome> RunAsync(
        TenantId tenant, VizSpec spec, SemanticModel model, IReadOnlyList<DatasetVersion> versions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(versions);
        if (model.TenantId != tenant || versions.Any(v => v.TenantId != tenant))
        {
            throw new InvalidOperationException("Model and dataset versions must belong to the calling tenant.");
        }

        using var activity = QueryTelemetry.ActivitySource.StartActivity("query.viz");
        activity?.SetTag("insightflow.dataset_version_id", spec.DatasetVersionId);
        var started = Stopwatch.GetTimestamp();

        CompiledQuery compiled;
        using (QueryTelemetry.ActivitySource.StartActivity("query.compile"))
        {
            compiled = compiler.Compile(spec, model, _dialect);
        }

        var key = QueryCacheKey.Create(tenant, spec, model, _dialect.Name, clock.GetUtcNow());
        QueryResult? cached;
        using (QueryTelemetry.ActivitySource.StartActivity("query.cache.get"))
        {
            cached = await cache.GetAsync(key, cancellationToken);
        }

        if (cached is not null)
        {
            return Complete(cached, compiled, fromCache: true, started, activity);
        }

        var result = await executor.ExecuteAsync(compiled, versions, cancellationToken);
        using (QueryTelemetry.ActivitySource.StartActivity("query.cache.set"))
        {
            await cache.SetAsync(key, result, cancellationToken);
        }

        return Complete(result, compiled, fromCache: false, started, activity);
    }

    public Task<QueryResult> PreviewAsync(DatasetVersion version, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var preview = new CompiledQuery(
            _dialect.Name,
            _dialect.ApplyLimit($"SELECT * FROM {_dialect.QuoteIdentifier("ds0")}", limit + 1),
            [],
            [],
            [new QuerySource("ds0", version.Id)],
            limit);
        return executor.ExecuteAsync(preview, [version], cancellationToken);
    }

    private static VizQueryOutcome Complete(QueryResult result, CompiledQuery compiled, bool fromCache, long started, Activity? activity)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        var outcome = fromCache ? "hit" : "miss";
        (fromCache ? QueryTelemetry.CacheHits : QueryTelemetry.CacheMisses).Add(1);
        QueryTelemetry.QueryDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("cache", outcome));
        activity?.SetTag("insightflow.cache", outcome);
        return new VizQueryOutcome(result, compiled, fromCache, elapsed);
    }
}
