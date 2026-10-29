using InsightFlow.Agent.Ai;
using InsightFlow.Agent.Documents;
using InsightFlow.Agent.Knowledge;
using InsightFlow.Agent.Sandbox;
using InsightFlow.Domain.Contracts.Documents;
using InsightFlow.Domain.Documents;
using InsightFlow.Repository;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.AgentService;

internal static partial class AgentEndpoints
{
    private const int MaxSheetTables = 40;

    /// <summary>
    /// Questions about the prepared spreadsheets. The tables are the caller's own prepared sheets, read from the stored index, never from the
    /// request; the model sees names and types only, and the SQL it writes runs in the locked sandbox over the sheets' Parquet files.
    /// </summary>
    private static async Task<Results<Ok<DocumentAnswerDto>, ProblemHttpResult>> AskDocumentsAsync(
        DocumentAskRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        IModelRouter router,
        SheetQuestionAnswerer answerer,
        ModelPriceBook prices,
        CancellationToken cancellationToken)
    {
        var question = request.Question?.Trim() ?? string.Empty;
        if (question.Length is 0 or > 2_000)
        {
            return Problem(StatusCodes.Status400BadRequest, "Ask a question of 1–2,000 characters.");
        }

        if (router.AvailableRoutes.Count == 0)
        {
            return NotConfigured();
        }

        var finished = await db.DocumentIngestRuns.Where(r => r.Status == IngestStatus.Succeeded).Select(r => r.Id).ToListAsync(cancellationToken);
        var sheets = await db.IndexedSheets.Where(s => s.Status == IndexedSheetStatus.Ready && s.VersionId != null && finished.Contains(s.RunId))
            .OrderBy(s => s.FileName).ThenBy(s => s.SheetName).Take(MaxSheetTables).ToListAsync(cancellationToken);
        if (sheets.Count == 0)
        {
            return Problem(StatusCodes.Status409Conflict, "No spreadsheets are ready yet. Prepare some first.");
        }

        var versionIds = sheets.Select(s => s.VersionId!.Value).ToList();
        var versions = await db.DatasetVersions.Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var tables = sheets
            .Where(s => versions.ContainsKey(s.VersionId!.Value))
            .Select(s => new SheetTable(s.TableName, s.FileName, s.SheetName, s.Rows, versions[s.VersionId!.Value].Schema.Columns, versions[s.VersionId!.Value]))
            .ToList();

        try
        {
            var answer = await answerer.AskAsync(
                tenant.TenantId!.Value, question, tables, [.. (request.History ?? []).Select(h => new QueryTurn(h.Role, h.Content))], cancellationToken);
            var result = answer.Run?.Preview;
            return TypedResults.Ok(new DocumentAnswerDto(
                answer.Explanation, answer.Sql, answer.Assumptions, answer.Clarification, result,
                answer.Run?.RowCount ?? 0, answer.Run?.Status == SandboxStatus.TooManyRows, answer.Error, [.. tables.Select(t => t.Alias)], answer.Evidence, answer.EvidenceRows, ToUsageDto(answer.Usage ?? AiUsage.None, prices), answer.Chart));
        }
        catch (TokenBudgetExceededException ex)
        {
            return Problem(StatusCodes.Status429TooManyRequests, ex.Message);
        }
    }
}

