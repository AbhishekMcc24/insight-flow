using System.Data;
using System.Data.Common;
using InsightFlow.Connectors;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Workspace;
using InsightFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Worker.Extracts;

/// <summary>
/// Drains the <c>extract_runs</c> queue. Claiming is a single atomic UPDATE … FOR UPDATE SKIP LOCKED across tenants
/// (system scope, queue columns only); each claimed run is then processed in its own DI scope entered into the run's
/// tenant, so connector, pipeline and metadata writes are all tenant-isolated.
/// </summary>
public sealed partial class ExtractRunProcessor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ExtractRunProcessor> logger)
{
    /// <summary>Runs left in <c>Running</c> this long (worker crashed/redeployed) are failed so users can retry.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    /// <summary>Processes up to <paramref name="maxRuns"/> pending runs; returns how many were processed.</summary>
    public async Task<int> ProcessPendingAsync(int maxRuns, CancellationToken cancellationToken)
    {
        await FailStaleRunsAsync(cancellationToken);

        var processed = 0;
        while (processed < maxRuns && await ClaimNextAsync(cancellationToken) is { } claimed)
        {
            await ProcessAsync(claimed.RunId, claimed.Tenant, cancellationToken);
            processed++;
        }

        return processed;
    }

    private async Task<(Guid RunId, TenantId Tenant)?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<JobCurrentTenant>().EnterSystem();
        var db = scope.ServiceProvider.GetRequiredService<InsightFlowDbContext>();

        var rows = await QueryAsync(db, """
            UPDATE extract_runs SET status = 'Running', started_at = @now
            WHERE id = (SELECT id FROM extract_runs WHERE status = 'Pending' ORDER BY requested_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id, tenant_id
            """, clock.GetUtcNow(), cancellationToken);
        return rows.Count == 0 ? null : (rows[0].Id, new TenantId(rows[0].TenantId));
    }

    private async Task FailStaleRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<JobCurrentTenant>().EnterSystem();
        var db = scope.ServiceProvider.GetRequiredService<InsightFlowDbContext>();
        var cutoff = clock.GetUtcNow() - StaleAfter;
        var failed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE extract_runs SET status = 'Failed', completed_at = {clock.GetUtcNow()},
                   error = 'The extract was interrupted (worker restart). Please run it again.'
            WHERE status = 'Running' AND started_at < {cutoff}
            """, cancellationToken);
        if (failed > 0)
        {
            LogStaleRunsFailed(logger, failed);
        }
    }

    private async Task ProcessAsync(Guid runId, TenantId tenant, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<JobCurrentTenant>().Enter(tenant);
        var db = scope.ServiceProvider.GetRequiredService<InsightFlowDbContext>();
        var pipeline = scope.ServiceProvider.GetRequiredService<ExtractPipeline>();

        var run = await db.ExtractRuns.SingleAsync(r => r.Id == runId, cancellationToken);
        var definition = await db.ExtractDefinitions.SingleAsync(d => d.Id == run.DefinitionId, cancellationToken);

        try
        {
            var profile = await ResolveProfileAsync(db, definition, cancellationToken);
            var previous = definition.LatestVersionId is { } latest
                ? await db.DatasetVersions.SingleAsync(v => v.Id == latest, cancellationToken)
                : null;

            var result = await pipeline.RunAsync(new ExtractRequest(profile, definition.SourceTable), previous, run.RequestedBy, cancellationToken);
            var version = result.Version;
            db.DatasetVersions.Add(version);

            var itemId = await UpsertDatasetItemAsync(db, definition, version, run.RequestedBy, cancellationToken);
            if (previous is not null)
            {
                await RetargetSemanticModelsAsync(db, previous.Id, version.Id, cancellationToken);
            }

            definition.RecordVersion(version, itemId);
            run.Succeed(version.Id, version.RowCount, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            LogRunSucceeded(logger, run.Id, version.Id, version.RowCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = ex switch
            {
                ConnectorException or DomainRuleException or NotImplementedException => ex.Message,
                _ => $"The extract failed unexpectedly (reference {run.Id:N}).",
            };

            if (ex is ConnectorException or DomainRuleException or NotImplementedException)
            {
                // Expected failures: the message can quote the source (e.g. a bad CSV value), so it is shown to the user, not logged.
                LogRunFailedExpected(logger, run.Id, ex.GetType().Name);
            }
            else
            {
                LogRunFailedUnexpected(logger, run.Id, ex);
            }

            db.ChangeTracker.Clear();
            var fresh = await db.ExtractRuns.SingleAsync(r => r.Id == runId, CancellationToken.None);
            fresh.Fail(message, clock.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static async Task<ConnectionProfile> ResolveProfileAsync(InsightFlowDbContext db, ExtractDefinition definition, CancellationToken ct)
    {
        if (definition.ConnectionProfileId is { } connectionId)
        {
            return await db.ConnectionProfiles.SingleOrDefaultAsync(c => c.Id == connectionId, ct)
                ?? throw new ConnectorException("The connection used by this dataset was deleted.");
        }

        var fileId = definition.StoredFileId ?? throw new InvalidOperationException("Definition has no source.");
        var file = await db.StoredFiles.SingleOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new ConnectorException("The uploaded file used by this dataset no longer exists.");
        return ConnectionProfile.ForStoredFile(definition.TenantId, definition.SourceKind, file.Id, file.OriginalName);
    }

    /// <summary>First run: create the dataset item next to the source (name clashes → "Name (2)"). Later runs: retarget it.</summary>
    private async Task<Guid> UpsertDatasetItemAsync(
        InsightFlowDbContext db, ExtractDefinition definition, DatasetVersion version, string requestedBy, CancellationToken ct)
    {
        if (definition.ContentItemId is { } existingId)
        {
            var existing = await db.ContentItems.IgnoreQueryFilters([InsightFlowDbContext.SoftDeleteFilter]).SingleAsync(i => i.Id == existingId, ct);
            existing.Retarget(version.Id);
            return existing.Id;
        }

        var folder = await db.Folders.SingleOrDefaultAsync(f => f.Id == definition.TargetFolderId, ct)
            ?? throw new ConnectorException("The folder for this dataset was deleted.");
        var names = await db.ContentItems.Where(i => i.FolderId == folder.Id).Select(i => i.Name).ToListAsync(ct);
        var item = ContentItem.Create(folder, NameConflicts.NextAvailable(definition.Name, names), ContentKind.Dataset, version.Id, requestedBy, clock.GetUtcNow());
        db.ContentItems.Add(item);
        return item.Id;
    }

    /// <summary>Semantic models follow the latest extract of their sources (old versions stay immutable for reproducibility).</summary>
    private async Task RetargetSemanticModelsAsync(InsightFlowDbContext db, Guid previousId, Guid newId, CancellationToken ct)
    {
        foreach (var record in await db.SemanticModels.ToListAsync(ct))
        {
            var model = record.ToDomain();
            if (model.Uses(previousId))
            {
                record.Update(model.ReplaceDatasetVersion(previousId, newId), clock.GetUtcNow());
            }
        }
    }

    private static async Task<List<(Guid Id, Guid TenantId)>> QueryAsync(InsightFlowDbContext db, string sql, DateTimeOffset now, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "now";
            parameter.Value = now;
            command.Parameters.Add(parameter);
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<(Guid, Guid)>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }

            return rows;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Extract run {RunId} succeeded: dataset version {DatasetVersionId}, {RowCount} rows")]
    private static partial void LogRunSucceeded(ILogger logger, Guid runId, Guid datasetVersionId, long rowCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Extract run {RunId} failed ({ErrorType}); the message was stored on the run")]
    private static partial void LogRunFailedExpected(ILogger logger, Guid runId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Extract run {RunId} failed unexpectedly")]
    private static partial void LogRunFailedUnexpected(ILogger logger, Guid runId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marked {Count} interrupted extract run(s) as failed")]
    private static partial void LogStaleRunsFailed(ILogger logger, int count);
}
