using InsightFlow.Connectors;
using InsightFlow.Contracts.Knowledge;
using InsightFlow.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Api.Knowledge;

public sealed partial class KnowledgeSourceService
{
    /// <summary>A scan newer than this is reused by the assistant; older ones are redone before it writes descriptions.</summary>
    public static readonly TimeSpan ProceduresMaxAge = TimeSpan.FromHours(6);

    /// <summary>When each tenant's scan last ran, so a scan that found nothing is not repeated at every question. Per process; a restart only means one more scan.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> LastScans = new();

    public async Task<KnowledgeProceduresDto> GetProceduresSummaryAsync(CancellationToken cancellationToken) =>
        await SummarizeProceduresAsync([], cancellationToken);

    /// <summary>
    /// Reads the stored procedures of every database the knowledge base takes tables from, and keeps the ones that use those tables.
    /// Only source text is read, never rows, and only through the saved connection (the connection, database and tables come from the
    /// stored knowledge base, never from the request). A database that cannot be read is reported and its earlier result is kept.
    /// </summary>
    public async Task<KnowledgeProceduresDto> ScanProceduresAsync(CancellationToken cancellationToken)
    {
        var tables = await db.KnowledgeTables.AsNoTracking()
            .Where(t => t.Source == DefinitionSource.Database && t.ConnectionId != null && t.DatabaseName != null)
            .ToListAsync(cancellationToken);
        var notes = new List<string>();
        var now = clock.GetUtcNow();

        foreach (var connectionGroup in tables.GroupBy(t => t.ConnectionId!.Value))
        {
            var refs = connectionGroup.Select(t => new ProcedureTableRef(t.Id, t.DatabaseName, t.SchemaName, t.Name)).ToList();
            foreach (var databaseGroup in connectionGroup.GroupBy(t => t.DatabaseName!, StringComparer.OrdinalIgnoreCase))
            {
                var databaseName = databaseGroup.Key;
                try
                {
                    var profile = WithDatabase(await LoadAsync(connectionGroup.Key, cancellationToken), databaseName);
                    if (connectors.Resolve(profile.Kind) is not IProcedureCatalog catalog)
                    {
                        notes.Add($"{databaseName}: reading stored procedures is not supported for {profile.Kind} yet.");
                        continue;
                    }

                    await EnsureHostAllowedAsync(profile.Kind, profile.Settings, cancellationToken);
                    var found = await catalog.ListProceduresAsync(profile, cancellationToken);

                    var kept = new List<KnowledgeProcedure>();
                    foreach (var procedure in found)
                    {
                        var used = ProcedureUsage.TablesUsed(procedure.Definition, databaseName, refs);
                        if (used.Count > 0)
                        {
                            kept.Add(KnowledgeProcedure.Create(Tenant, connectionGroup.Key, databaseName, procedure.Schema, procedure.Name, procedure.Definition, used, now));
                        }
                    }

                    // Replace this database's earlier result in one save.
                    var old = await db.KnowledgeProcedures.Where(p => p.ConnectionId == connectionGroup.Key).ToListAsync(cancellationToken);
                    db.KnowledgeProcedures.RemoveRange(old.Where(p => string.Equals(p.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase)));
                    db.KnowledgeProcedures.AddRange(kept);
                    await db.SaveChangesAsync(cancellationToken);
                    LogProceduresScanned(logger, found.Count, kept.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogProceduresFailed(logger, ex.GetType().Name);
                    notes.Add(ex is ConnectorException or ApiProblemException
                        ? $"{databaseName}: {ex.Message}"
                        : $"{databaseName}: the stored procedures could not be read (the login may need the VIEW DEFINITION permission).");
                }
            }
        }

        LastScans[Tenant.Value] = now;
        return await SummarizeProceduresAsync(notes, cancellationToken);
    }

    private async Task<KnowledgeProceduresDto> SummarizeProceduresAsync(IReadOnlyList<string> notes, CancellationToken cancellationToken)
    {
        var procedures = await db.KnowledgeProcedures.AsNoTracking().Select(p => new { p.TableIds, p.ScannedAt }).ToListAsync(cancellationToken);
        var total = await db.KnowledgeTables.CountAsync(t => t.Source == DefinitionSource.Database, cancellationToken);
        var covered = procedures.SelectMany(p => p.TableIds).Distinct().Count();
        var scanned = procedures.Select(p => (DateTimeOffset?)p.ScannedAt).Append(LastScans.TryGetValue(Tenant.Value, out var last) ? last : null).Max();
        return new KnowledgeProceduresDto(procedures.Count, covered, total, scanned, notes);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored procedures scanned: {Found} read, {Kept} use knowledge-base tables")]
    private static partial void LogProceduresScanned(ILogger logger, int found, int kept);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading stored procedures failed ({ExceptionType})")]
    private static partial void LogProceduresFailed(ILogger logger, string exceptionType);
}
