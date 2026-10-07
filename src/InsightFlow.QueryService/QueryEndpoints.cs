using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Threads;
using InsightFlow.Persistence;
using InsightFlow.Query;
using InsightFlow.Query.Compilation;
using InsightFlow.Query.Modeling;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.QueryService;

/// <summary>
/// HTTP surface of the query engine. Metadata is loaded through the tenant-filtered DbContext, so a dataset version or
/// model of another tenant is simply "not found"; the engine itself re-checks tenant ownership before executing.
/// </summary>
internal static class QueryEndpoints
{
    public static RouteGroupBuilder MapQueryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/viz", RunVizAsync)
            .WithName("RunVizQuery")
            .WithSummary("Compile a VizSpec, run it on DuckDB (or serve it from cache) and return rows plus SQL.")
            .RequireAuthorization(InsightFlowPolicies.CanView);

        group.MapPost("/preview", PreviewAsync)
            .WithName("PreviewDatasetVersion")
            .WithSummary("First rows and column types of a dataset version.")
            .RequireAuthorization(InsightFlowPolicies.CanExplore);

        return group;
    }

    private static async Task<Results<Ok<VizQueryResponse>, NotFound<ProblemDetails>, ProblemHttpResult>> RunVizAsync(
        VizQueryRequest request,
        InsightFlowDbContext db,
        ICurrentTenant currentTenant,
        IVizQueryEngine engine,
        CancellationToken cancellationToken)
    {
        var tenant = currentTenant.TenantId!.Value;
        var version = await db.DatasetVersions.FindAsync([request.Spec.DatasetVersionId], cancellationToken);
        if (version is null)
        {
            return NotFound("Dataset version not found.");
        }

        var model = await ResolveModelAsync(db, request.SemanticModelId, version, cancellationToken);
        if (model is null)
        {
            return NotFound("Semantic model not found.");
        }

        var versionIds = model.Tables.Select(t => t.SourceDatasetVersionId).Distinct().ToList();
        var versions = await db.DatasetVersions.Where(v => versionIds.Contains(v.Id)).ToListAsync(cancellationToken);
        if (versions.Count != versionIds.Count)
        {
            return NotFound("A dataset version used by the semantic model was not found.");
        }

        try
        {
            var outcome = await engine.RunAsync(tenant, request.Spec, model, versions, cancellationToken);
            return TypedResults.Ok(new VizQueryResponse(outcome.Result, outcome.Query.Sql, outcome.FromCache, outcome.Duration.TotalMilliseconds));
        }
        catch (VizSpecValidationException ex)
        {
            return TypedResults.Problem(
                title: "The chart specification is invalid.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                type: "https://insightflow.dev/problems/invalid-viz-spec",
                extensions: new Dictionary<string, object?>
                {
                    ["errors"] = ex.Errors.Select(e => new Dictionary<string, string> { ["code"] = e.Code, ["path"] = e.Path, ["message"] = e.Message }).ToList(),
                });
        }
        catch (TimeoutException)
        {
            return TypedResults.Problem(title: "The query took too long.", statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<Results<Ok<PreviewResponse>, NotFound<ProblemDetails>, ValidationProblem>> PreviewAsync(
        PreviewRequest request,
        InsightFlowDbContext db,
        IVizQueryEngine engine,
        CancellationToken cancellationToken)
    {
        if (request.Limit is < 1 or > PreviewRequest.MaxLimit)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = [$"Limit must be between 1 and {PreviewRequest.MaxLimit}."],
            });
        }

        var version = await db.DatasetVersions.FindAsync([request.DatasetVersionId], cancellationToken);
        if (version is null)
        {
            return NotFound("Dataset version not found.");
        }

        var result = await engine.PreviewAsync(version, request.Limit, cancellationToken);
        return TypedResults.Ok(new PreviewResponse(version.Id, result, version.RowCount));
    }

    /// <summary>The requested model; else the tenant's model that contains the version; else an implicit single-table model.</summary>
    private static async Task<SemanticModel?> ResolveModelAsync(
        InsightFlowDbContext db, Guid? semanticModelId, DatasetVersion version, CancellationToken cancellationToken)
    {
        if (semanticModelId is { } id)
        {
            var record = await db.SemanticModels.FindAsync([id], cancellationToken);
            return record?.ToDomain();
        }

        // TODO(roy): index models by dataset version (JSONB path or join table) once tenants have many models.
        var models = await db.SemanticModels.AsNoTracking().ToListAsync(cancellationToken);
        return models.Select(m => m.ToDomain()).FirstOrDefault(m => m.Tables.Any(t => t.SourceDatasetVersionId == version.Id))
               ?? ImplicitSemanticModel.For(version);
    }

    private static NotFound<ProblemDetails> NotFound(string detail) =>
        TypedResults.NotFound(new ProblemDetails { Title = "Not found", Detail = detail, Status = StatusCodes.Status404NotFound });
}
