using System.Data;
using System.Data.Common;
using InsightFlow.Connectors;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Documents;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Workspace;
using InsightFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Worker.Documents;

/// <summary>
/// Drains the <c>document_ingest_runs</c> queue. Each claimed run opens its workbooks, parses every sheet with DuckDB and saves the sheets as
/// tables. When DuckDB cannot parse something the run stops in <c>NeedsInput</c> with the problems and waits; once the person has answered,
/// it is queued again and continues with the sheets that are not done yet (finished sheets are never read twice).
/// </summary>
public sealed partial class DocumentIngestProcessor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DocumentIngestProcessor> logger)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private static readonly string[] SupportedExtensions = [".xlsx", ".xlsm", ".xls"];

    private sealed record SheetPlan(ContentItem Item, StoredFile File, string Extension, IReadOnlyList<string> Sheets, IngestProblem? Blocked);

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
            UPDATE document_ingest_runs SET status = 'Running', started_at = COALESCE(started_at, @now)
            WHERE id = (SELECT id FROM document_ingest_runs WHERE status = 'Pending' ORDER BY requested_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id, tenant_id
            """, clock.GetUtcNow(), cancellationToken);
        return rows.Count == 0 ? null : (rows[0].Id, new TenantId(rows[0].TenantId));
    }

    private async Task FailStaleRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<JobCurrentTenant>().EnterSystem();
        var db = scope.ServiceProvider.GetRequiredService<InsightFlow.Persistence.InsightFlowDbContext>();
        var now = clock.GetUtcNow();
        var cutoff = now - StaleAfter;
        var failed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE document_ingest_runs SET status = 'Failed', completed_at = {now},
                   error = 'The preparation was interrupted (worker restart). Please prepare the files again.'
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
        var reader = scope.ServiceProvider.GetRequiredService<SpreadsheetReader>();
        var files = scope.ServiceProvider.GetRequiredService<ISourceFileAccessor>();

        var run = await db.DocumentIngestRuns.SingleAsync(r => r.Id == runId, cancellationToken);
        try
        {
            run.Start(clock.GetUtcNow());
            run.SetStep(IngestSteps.Open, IngestStepState.Running, "Looking at the files…");
            await db.SaveChangesAsync(cancellationToken);

            // 1) Open every workbook and list its sheets.
            var plans = await PlanAsync(db, files, run, tenant, cancellationToken);
            var totalSheets = plans.Sum(p => p.Sheets.Count);
            run.SetStep(IngestSteps.Open, IngestStepState.Done, $"{plans.Count} workbook{(plans.Count == 1 ? "" : "s")}, {totalSheets} sheet{(totalSheets == 1 ? "" : "s")}");
            run.SetStep(IngestSteps.Parse, IngestStepState.Running, $"0 of {totalSheets} sheets");
            await db.SaveChangesAsync(cancellationToken);

            // 2) Parse each sheet with DuckDB and save it as a table.
            var problems = run.Problems.Where(p => p.IsResolved).ToList(); // answered problems are kept as the run's history
            var fresh = new List<IngestProblem>();
            var doneBefore = (await db.IndexedSheets.Where(s => s.RunId == run.Id).ToListAsync(cancellationToken))
                .Select(s => (s.ContentItemId, s.SheetName)).ToHashSet();
            var usedNames = (await db.IndexedSheets.Where(s => s.RunId != run.Id && !run.ItemIds.Contains(s.ContentItemId)).Select(s => s.TableName).ToListAsync(cancellationToken))
                .Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var kept in db.IndexedSheets.Local.Where(s => s.RunId == run.Id && s.TableName.Length > 0))
            {
                usedNames.Add(kept.TableName);
            }

            var now = () => clock.GetUtcNow();
            var finished = doneBefore.Count;
            long rows = (await db.IndexedSheets.Where(s => s.RunId == run.Id && s.Status == IndexedSheetStatus.Ready).Select(s => s.Rows).ToListAsync(cancellationToken)).Sum();
            var ready = (await db.IndexedSheets.CountAsync(s => s.RunId == run.Id && s.Status == IndexedSheetStatus.Ready, cancellationToken));

            foreach (var plan in plans)
            {
                if (plan.Blocked is { } blocked)
                {
                    var answer = problems.FirstOrDefault(p => p.ItemId == plan.Item.Id && p.Sheet is null && p.Kind == blocked.Kind);
                    if (answer is null)
                    {
                        fresh.Add(blocked);
                    }
                    else if (!doneBefore.Contains((plan.Item.Id, "(file)")))
                    {
                        db.IndexedSheets.Add(IndexedSheet.Skipped(tenant, plan.Item.Id, plan.File.Id, plan.Item.Name.Value, "(file)", "Skipped: " + blocked.Message, run.Id, now()));
                        await db.SaveChangesAsync(cancellationToken);
                    }

                    continue;
                }

                await using var local = await files.DownloadAsync(tenant, plan.File.Id, plan.Extension, cancellationToken);
                var workbookPath = local.Path;
                string? converted = null;
                if (plan.Extension == ".xls")
                {
                    converted = Path.Combine(Path.GetTempPath(), $"insightflow-{Guid.NewGuid():N}.xlsx");
                    LegacyWorkbook.ConvertToXlsx(local.Path, converted);
                    workbookPath = converted;
                }

                try
                {
                foreach (var sheet in plan.Sheets)
                {
                    if (doneBefore.Contains((plan.Item.Id, sheet)))
                    {
                        continue;
                    }

                    var answers = problems.Where(p => p.ItemId == plan.Item.Id && p.Sheet == sheet).ToList();
                    if (answers.Any(a => a.Chosen == ProblemChoices.SkipSheet) || problems.Any(p => p.ItemId == plan.Item.Id && p.Chosen == ProblemChoices.SkipFile))
                    {
                        db.IndexedSheets.Add(IndexedSheet.Skipped(tenant, plan.Item.Id, plan.File.Id, plan.Item.Name.Value, sheet, "Skipped at your request.", run.Id, now()));
                        await db.SaveChangesAsync(cancellationToken);
                        continue;
                    }

                    var settings = SettingsFrom(answers);
                    var outcome = await reader.ParseSheetAsync(tenant, workbookPath, sheet, settings, run.RequestedBy, cancellationToken);
                    switch (outcome)
                    {
                        case SheetParsed parsed:
                            db.DatasetVersions.Add(parsed.Version);
                            var table = SpreadsheetReader.TableName(plan.Item.Name.Value, sheet, usedNames);
                            db.IndexedSheets.Add(IndexedSheet.Ready(
                                tenant, plan.Item.Id, plan.File.Id, plan.Item.Name.Value, sheet, table, parsed.Version.Id, parsed.Rows, parsed.Columns, Note(settings.Describe(), parsed.Layout, parsed.TotalRows), run.Id, now()));
                            rows += parsed.Rows;
                            ready++;
                            break;
                        case SheetEmpty:
                            db.IndexedSheets.Add(IndexedSheet.Skipped(tenant, plan.Item.Id, plan.File.Id, plan.Item.Name.Value, sheet, "The sheet is empty.", run.Id, now()));
                            break;
                        case SheetFailed failed:
                            fresh.Add(new IngestProblem(Guid.CreateVersion7(), plan.Item.Id, plan.Item.Name.Value, sheet, failed.Kind, failed.Message, failed.Technical, failed.Options));
                            break;
                    }

                    finished++;
                    run.SetStep(IngestSteps.Parse, IngestStepState.Running, $"{finished} of {totalSheets} sheets");
                    run.SetStep(IngestSteps.Save, IngestStepState.Running, $"{ready} table{(ready == 1 ? "" : "s")} saved");
                    await db.SaveChangesAsync(cancellationToken);
                }
                }
                finally
                {
                    if (converted is not null)
                    {
                        File.Delete(converted);
                    }
                }
            }

            if (fresh.Count > 0)
            {
                run.SetStep(IngestSteps.Parse, IngestStepState.Running, $"{fresh.Count} problem{(fresh.Count == 1 ? "" : "s")} need{(fresh.Count == 1 ? "s" : "")} your decision");
                run.NeedInput([.. problems, .. fresh]);
                await db.SaveChangesAsync(cancellationToken);
                LogNeedsInput(logger, run.Id, fresh.Count);
                return;
            }

            // 3) Done: the new tables replace those of the same files from earlier runs.
            var older = await db.IndexedSheets.Where(s => run.ItemIds.Contains(s.ContentItemId) && s.RunId != run.Id).ToListAsync(cancellationToken);
            db.IndexedSheets.RemoveRange(older);
            run.SetStep(IngestSteps.Parse, IngestStepState.Done, $"{finished} of {totalSheets} sheets");
            run.SetStep(IngestSteps.Save, IngestStepState.Done, $"{ready} table{(ready == 1 ? "" : "s")} saved");
            run.Succeed(ready, rows, $"{ready} table{(ready == 1 ? "" : "s")}, {rows:N0} rows. Ask your questions below.", clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            LogSucceeded(logger, run.Id, ready, rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, run.Id, ex);
            db.ChangeTracker.Clear();
            var fresh = await db.DocumentIngestRuns.SingleAsync(r => r.Id == runId, CancellationToken.None);
            fresh.Fail($"The preparation failed unexpectedly (reference {runId:N}).", clock.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static string? Note(string? settings, SheetLayout? layout, long totalRows)
    {
        var parts = new[] { layout?.Describe(), totalRows > 0 ? $"{totalRows} subtotal/total rows flagged in is_total_row" : null, settings }.OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>The settings a person's earlier answers add up to for one sheet.</summary>
    internal static SheetSettings SettingsFrom(IEnumerable<IngestProblem> answers)
    {
        var settings = new SheetSettings();
        foreach (var a in answers)
        {
            settings = a.Chosen switch
            {
                ProblemChoices.AsText => settings with { AllVarchar = true },
                ProblemChoices.IgnoreErrors => settings with { IgnoreErrors = true },
                ProblemChoices.NoHeader => settings with { Header = false },
                ProblemChoices.Range => settings with { Range = a.Text },
                _ => settings,
            };
        }

        return settings;
    }

    /// <summary>Downloads each chosen file once to list its sheets; files that cannot be read become file-level problems.</summary>
    private static async Task<List<SheetPlan>> PlanAsync(
        InsightFlowDbContext db, ISourceFileAccessor files, DocumentIngestRun run, TenantId tenant, CancellationToken cancellationToken)
    {
        var items = await db.ContentItems.Where(i => run.ItemIds.Contains(i.Id) && i.Kind == ContentKind.File).ToListAsync(cancellationToken);
        var plans = new List<SheetPlan>();
        foreach (var item in items.OrderBy(i => i.Name.Value, StringComparer.OrdinalIgnoreCase))
        {
            var stored = await db.StoredFiles.SingleOrDefaultAsync(f => f.Id == item.TargetId, cancellationToken);
            if (stored is null)
            {
                continue;
            }

            var extension = Path.GetExtension(item.Name.Value).ToLowerInvariant();
            if (!SupportedExtensions.Contains(extension))
            {
                var unsupported = SpreadsheetProblems.UnsupportedFormat(extension);
                plans.Add(new SheetPlan(item, stored, extension, [], Problem(item, null, unsupported)));
                continue;
            }

            try
            {
                await using var local = await files.DownloadAsync(tenant, stored.Id, extension, cancellationToken);
                if (extension == ".xls")
                {
                    var temp = Path.Combine(Path.GetTempPath(), $"insightflow-{Guid.NewGuid():N}.xlsx");
                    try
                    {
                        LegacyWorkbook.ConvertToXlsx(local.Path, temp);
                        plans.Add(new SheetPlan(item, stored, extension, SpreadsheetReader.ListSheets(temp), null));
                    }
                    finally
                    {
                        File.Delete(temp);
                    }
                }
                else
                {
                    plans.Add(new SheetPlan(item, stored, extension, SpreadsheetReader.ListSheets(local.Path), null));
                }
            }
            catch (SpreadsheetException ex)
            {
                var outcome = SpreadsheetProblems.Classify(ex, "(workbook)", new SheetSettings());
                plans.Add(new SheetPlan(item, stored, extension, [], outcome is SheetFailed failed ? Problem(item, null, failed) : null));
            }
            catch (ConnectorException ex)
            {
                var gone = new SheetFailed(SpreadsheetProblems.Unreadable, ex.Message, null, [new ProblemOption(ProblemChoices.SkipFile, "Skip this file")]);
                plans.Add(new SheetPlan(item, stored, extension, [], Problem(item, null, gone)));
            }
        }

        return plans;
    }

    private static IngestProblem Problem(ContentItem item, string? sheet, SheetFailed failed) =>
        new(Guid.CreateVersion7(), item.Id, item.Name.Value, sheet, failed.Kind, failed.Message, failed.Technical, failed.Options);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Document run {RunId} succeeded: {Tables} tables, {Rows} rows")]
    private static partial void LogSucceeded(ILogger logger, Guid runId, int tables, long rows);

    [LoggerMessage(Level = LogLevel.Information, Message = "Document run {RunId} needs input: {Problems} problem(s)")]
    private static partial void LogNeedsInput(ILogger logger, Guid runId, int problems);

    [LoggerMessage(Level = LogLevel.Error, Message = "Document run {RunId} failed unexpectedly")]
    private static partial void LogFailed(ILogger logger, Guid runId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marked {Count} interrupted document run(s) as failed")]
    private static partial void LogStaleRunsFailed(ILogger logger, int count);
}

