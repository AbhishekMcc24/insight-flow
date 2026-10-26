using InsightFlow.Contracts.Documents;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace InsightFlow.Api.Documents;

/// <summary>
/// <c>/api/v1/documents</c>: prepare uploaded Excel files for questions and follow the pipeline. Everyone who can read the workspace
/// sees the files and the progress; preparing files and answering problems needs the right to create content.
/// </summary>
internal static class DocumentEndpoints
{
    public static RouteGroupBuilder MapDocumentEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanView);

        group.MapGet("/files", (DocumentService documents, CancellationToken ct) => documents.ListFilesAsync(ct)).WithName("ListSpreadsheetFiles");
        group.MapGet("/summary", (DocumentService documents, CancellationToken ct) => documents.GetSummaryAsync(ct)).WithName("GetDocumentSummary");
        group.MapGet("/runs/latest", async Task<Results<Ok<IngestRunDto>, NoContent>> (DocumentService documents, CancellationToken ct) =>
            await documents.GetLatestRunAsync(ct) is { } run ? TypedResults.Ok(run) : TypedResults.NoContent()).WithName("GetLatestIngestRun");
        group.MapGet("/runs/{runId:guid}", async Task<Results<Ok<IngestRunDto>, NotFound>> (Guid runId, DocumentService documents, CancellationToken ct) =>
            await documents.GetRunAsync(runId, ct) is { } run ? TypedResults.Ok(run) : TypedResults.NotFound()).WithName("GetIngestRun");

        group.MapPost("/runs", async (StartIngestRequest request, DocumentService documents, CancellationToken ct) =>
        {
            var run = await documents.StartRunAsync(request, ct);
            return TypedResults.Accepted($"/api/v1/documents/runs/{run.Id}", run);
        }).WithName("StartIngestRun").RequireAuthorization(InsightFlowPolicies.CanCreate);

        group.MapPost("/runs/{runId:guid}/problems/{problemId:guid}/resolve", (Guid runId, Guid problemId, ResolveProblemRequest request, DocumentService documents, CancellationToken ct) =>
            documents.ResolveAsync(runId, problemId, request, ct)).WithName("ResolveIngestProblem").RequireAuthorization(InsightFlowPolicies.CanCreate);

        group.MapDelete("/files/{itemId:guid}/index", async (Guid itemId, DocumentService documents, CancellationToken ct) =>
        {
            await documents.RemoveFromIndexAsync(itemId, ct);
            return TypedResults.NoContent();
        }).WithName("RemoveFileFromIndex").RequireAuthorization(InsightFlowPolicies.CanCreate);

        return group;
    }
}
