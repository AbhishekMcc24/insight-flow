using InsightFlow.Domain.Contracts.Documents;
using InsightFlow.Domain.Documents;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Workspace;
using InsightFlow.Repository;
using InsightFlow.Foundation.Security;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Logic.Documents;

/// <summary>
/// Spreadsheets for questions: lists the Excel files the caller can read, queues a preparation run for the ones they choose, shows its steps
/// and the problems DuckDB hit, records the person's answers, and reports what is ready. The heavy work (opening workbooks, parsing sheets
/// with DuckDB) is done by the Worker; this service only reads and writes the run.
/// </summary>
public sealed partial class DocumentService(
    InsightFlowDbContext db,
    ITenantContext caller,
    IContentPermissionEvaluator permissions,
    TimeProvider clock,
    ILogger<DocumentService> logger)
{
    private static readonly string[] ExcelExtensions = [".xlsx", ".xlsm", ".xls"];

    private TenantId Tenant => new(caller.TenantId ?? throw new InvalidOperationException("No tenant in scope."));

    private string UserId => caller.UserId ?? throw new InvalidOperationException("No user in scope.");

    private WorkspacePrincipal Principal => new(Tenant, UserId, HighestRole());

    private TenantRole HighestRole()
    {
        var roles = caller.Roles;
        return roles.Contains(InsightFlowRoles.TenantAdmin) ? TenantRole.TenantAdmin
            : roles.Contains(InsightFlowRoles.Creator) ? TenantRole.Creator
            : roles.Contains(InsightFlowRoles.Explorer) ? TenantRole.Explorer
            : TenantRole.Viewer;
    }

    /// <summary>Every uploaded Excel file the caller can read, with how far each one is.</summary>
    public async Task<IReadOnlyList<SpreadsheetFileDto>> ListFilesAsync(CancellationToken cancellationToken)
    {
        var folders = (await db.Folders.ToListAsync(cancellationToken)).Where(f => permissions.CanRead(f, Principal)).ToDictionary(f => f.Id);
        var items = (await db.ContentItems.Where(i => i.Kind == ContentKind.File).ToListAsync(cancellationToken))
            .Where(i => folders.ContainsKey(i.FolderId) && IsExcel(i.Name.Value))
            .ToList();
        var stored = await db.StoredFiles.Where(f => items.Select(i => i.TargetId).Contains(f.Id)).ToDictionaryAsync(f => f.Id, cancellationToken);
        // Only sheets of finished runs count: a run waiting for answers must not offer half-read tables to questions.
        var finished = await db.DocumentIngestRuns.Where(r => r.Status == IngestStatus.Succeeded).Select(r => r.Id).ToListAsync(cancellationToken);
        var sheets = (await db.IndexedSheets.Where(s => finished.Contains(s.RunId)).ToListAsync(cancellationToken)).GroupBy(s => s.ContentItemId).ToDictionary(g => g.Key);
        var active = await db.DocumentIngestRuns
            .Where(r => r.Status == IngestStatus.Pending || r.Status == IngestStatus.Running || r.Status == IngestStatus.NeedsInput)
            .ToListAsync(cancellationToken);

        return [.. items
            .OrderBy(i => PathOf(folders, i.FolderId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name.Value, StringComparer.OrdinalIgnoreCase)
            .Select(i =>
            {
                var run = active.FirstOrDefault(r => r.ItemIds.Contains(i.Id));
                var mine = sheets.GetValueOrDefault(i.Id)?.ToList() ?? [];
                var ready = mine.Where(s => s.Status == IndexedSheetStatus.Ready).ToList();
                var state = run is not null ? (run.Status == IngestStatus.NeedsInput ? "NeedsInput" : "Preparing")
                    : ready.Count > 0 ? "Ready"
                    : mine.Count > 0 ? "Skipped"
                    : "NotPrepared";
                return new SpreadsheetFileDto(
                    i.Id, i.Name.Value, PathOf(folders, i.FolderId), stored.GetValueOrDefault(i.TargetId)?.SizeBytes ?? 0, true, state,
                    ready.Count, ready.Sum(s => s.Rows), mine.Count == 0 ? null : mine.Max(s => s.IndexedAt), null);
            })];
    }

    /// <summary>Queues a preparation run for the chosen files. A run waiting for answers is replaced by the new one.</summary>
    public async Task<IngestRunDto> StartRunAsync(StartIngestRequest request, CancellationToken cancellationToken)
    {
        var ids = (request.ItemIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "nothing_chosen", "Choose at least one Excel file.");
        }

        var folders = (await db.Folders.ToListAsync(cancellationToken)).Where(f => permissions.CanRead(f, Principal)).Select(f => f.Id).ToHashSet();
        var items = await db.ContentItems.Where(i => ids.Contains(i.Id) && i.Kind == ContentKind.File).ToListAsync(cancellationToken);
        if (items.Count != ids.Count || items.Any(i => !folders.Contains(i.FolderId)))
        {
            throw ApiProblemException.NotFound("One of the files");
        }

        if (items.FirstOrDefault(i => !IsExcel(i.Name.Value)) is { } notExcel)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "not_excel", $"“{notExcel.Name.Value}” is not an Excel file. Only Excel workbooks can be prepared for now.");
        }

        var open = await db.DocumentIngestRuns
            .Where(r => r.Status == IngestStatus.Pending || r.Status == IngestStatus.Running || r.Status == IngestStatus.NeedsInput)
            .ToListAsync(cancellationToken);
        if (open.Any(r => r.Status is IngestStatus.Pending or IngestStatus.Running))
        {
            throw ApiProblemException.Conflict("Spreadsheets are already being prepared. Wait for that to finish first.");
        }

        foreach (var waiting in open)
        {
            waiting.Fail("Replaced by a newer preparation.", clock.GetUtcNow());
        }

        var run = DocumentIngestRun.Create(Tenant, UserId, ids, clock.GetUtcNow());
        db.DocumentIngestRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        LogQueued(logger, run.Id, ids.Count);
        return await ToDtoAsync(run, cancellationToken);
    }

    public async Task<IngestRunDto?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.DocumentIngestRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        return run is null ? null : await ToDtoAsync(run, cancellationToken);
    }

    /// <summary>The most recent run (what the page shows when it opens).</summary>
    public async Task<IngestRunDto?> GetLatestRunAsync(CancellationToken cancellationToken)
    {
        var run = await db.DocumentIngestRuns.OrderByDescending(r => r.RequestedAt).FirstOrDefaultAsync(cancellationToken);
        return run is null ? null : await ToDtoAsync(run, cancellationToken);
    }

    /// <summary>Records the person's answer to one problem; when all are answered the run is queued again.</summary>
    public async Task<IngestRunDto> ResolveAsync(Guid runId, Guid problemId, ResolveProblemRequest request, CancellationToken cancellationToken)
    {
        var run = await db.DocumentIngestRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken) ?? throw ApiProblemException.NotFound("The run");
        if (run.Status != IngestStatus.NeedsInput)
        {
            throw ApiProblemException.Conflict("This run is not waiting for an answer.");
        }

        run.Resolve(problemId, request.Option, request.Text, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(run, cancellationToken);
    }

    /// <summary>Takes a file out of the question index (its tables are removed; the upload stays).</summary>
    public async Task RemoveFromIndexAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var sheets = await db.IndexedSheets.Where(s => s.ContentItemId == itemId).ToListAsync(cancellationToken);
        db.IndexedSheets.RemoveRange(sheets);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DocumentSummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var finished = await db.DocumentIngestRuns.Where(r => r.Status == IngestStatus.Succeeded).Select(r => r.Id).ToListAsync(cancellationToken);
        var ready = await db.IndexedSheets.Where(s => s.Status == IndexedSheetStatus.Ready && finished.Contains(s.RunId)).OrderBy(s => s.FileName).ThenBy(s => s.SheetName).ToListAsync(cancellationToken);
        return new DocumentSummaryDto(
            ready.Select(s => s.ContentItemId).Distinct().Count(),
            ready.Count,
            ready.Sum(s => s.Rows),
            [.. ready.Select(s => new SheetTableDto(s.TableName, s.FileName, s.SheetName, s.Rows, s.Columns, s.Note))]);
    }

    private async Task<IngestRunDto> ToDtoAsync(DocumentIngestRun run, CancellationToken cancellationToken)
    {
        var names = await db.ContentItems.Where(i => run.ItemIds.Contains(i.Id)).Select(i => i.Name).ToListAsync(cancellationToken);
        var sheets = await db.IndexedSheets.Where(s => s.RunId == run.Id).OrderBy(s => s.FileName).ThenBy(s => s.SheetName).ToListAsync(cancellationToken);
        return new IngestRunDto(
            run.Id,
            run.Status.ToString(),
            run.RequestedAt,
            run.CompletedAt,
            [.. names.Select(n => n.Value).Order(StringComparer.OrdinalIgnoreCase)],
            [.. run.Steps.Select(s => new IngestStepDto(s.Key, s.Title, s.State.ToString(), s.Detail))],
            [.. run.Problems.Select(p => new IngestProblemDto(
                p.Id, p.ItemId, p.FileName, p.Sheet, p.Kind, p.Message, p.Technical,
                [.. p.Options.Select(o => new ProblemOptionDto(o.Key, o.Label, o.NeedsText, o.TextHint))], p.Chosen, p.Text))],
            [.. sheets.Select(s => new IngestSheetDto(s.FileName, s.SheetName, s.TableName.Length == 0 ? null : s.TableName, s.Status.ToString(), s.Rows, s.Columns, s.Note))],
            sheets.Count(s => s.Status == IndexedSheetStatus.Ready),
            sheets.Where(s => s.Status == IndexedSheetStatus.Ready).Sum(s => s.Rows),
            run.Error);
    }

    private static bool IsExcel(string fileName) => ExcelExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    private static string PathOf(Dictionary<Guid, Folder> folders, Guid folderId)
    {
        var parts = new List<string>();
        for (Guid? id = folderId; id is { } current && folders.TryGetValue(current, out var folder); id = folder.ParentId)
        {
            parts.Add(folder.Name.Value);
        }

        parts.Reverse();
        return string.Join(" / ", parts);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Spreadsheet preparation {RunId} queued for {Files} file(s)")]
    private static partial void LogQueued(ILogger logger, Guid runId, int files);
}


