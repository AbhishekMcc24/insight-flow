using System.Net.ServerSentEvents;
using System.Text.RegularExpressions;
using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Analyst;
using InsightFlow.Agents.DerivedFields;
using InsightFlow.Agents.Sandbox;
using InsightFlow.Contracts.Agents;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Threads;
using InsightFlow.Persistence;
using InsightFlow.Query;
using InsightFlow.Query.Compilation;
using InsightFlow.Query.Modeling;
using InsightFlow.Query.Storage;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.AgentService;

/// <summary>
/// HTTP surface of the agents. Dataset versions and models are loaded through the tenant-filtered DbContext, so agents only
/// ever see the caller's data. The analyst streams Server-Sent Events; the derived-field flow returns the finished result.
/// </summary>
internal static partial class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanExplore);

        group.MapPost("/analyst", AnalystAsync)
            .WithName("AskAnalyst")
            .WithSummary("Ask a question about a dataset; streams text, tool, sql (+preview), chart, done/error events (SSE).");

        group.MapPost("/derived-field", DerivedFieldAsync)
            .WithName("DeriveField")
            .WithSummary("Derive a field that does not exist yet from a plain-English hint and chart it.");

        return group;
    }

    private static async Task<IResult> AnalystAsync(
        AnalystRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        IModelRouter router,
        AnalystAgent analyst,
        CancellationToken cancellationToken)
    {
        if (router.AvailableRoutes.Count == 0)
        {
            return NotConfigured();
        }

        var version = await db.DatasetVersions.FindAsync([request.DatasetVersionId], cancellationToken);
        if (version is null)
        {
            return TypedResults.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 4_000)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["question"] = ["Ask a question of up to 4,000 characters."] });
        }

        var model = await ResolveModelAsync(db, request.SemanticModelId, version, cancellationToken);
        var context = new AnalystContext(tenant.TenantId!.Value, version, model);
        var events = analyst.RunAsync(context, request.Question, cancellationToken);
        return TypedResults.ServerSentEvents(ToSse(events, cancellationToken));
    }

    private static async Task<Results<Ok<DerivedFieldResponse>, NotFound, ProblemHttpResult>> DerivedFieldAsync(
        DerivedFieldRequest request,
        InsightFlowDbContext db,
        ICurrentTenant currentTenant,
        ITenantContext caller,
        IModelRouter router,
        DerivedFieldPlanner planner,
        IExtractStore extracts,
        IVizQueryEngine engine,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (router.AvailableRoutes.Count == 0)
        {
            return NotConfigured();
        }

        if (!FieldNamePattern().IsMatch(request.FieldName ?? string.Empty) || string.IsNullOrWhiteSpace(request.Hint) || request.Hint.Length > 2_000)
        {
            return Problem(StatusCodes.Status400BadRequest, "Use a field name of letters, digits, spaces or underscores (max 63) and a hint of up to 2,000 characters.");
        }

        var tenant = currentTenant.TenantId!.Value;
        var version = await db.DatasetVersions.FindAsync([request.Spec.DatasetVersionId], cancellationToken);
        if (version is null)
        {
            return TypedResults.NotFound();
        }

        var model = await ResolveModelAsync(db, null, version, cancellationToken);
        var blocking = Domain.Validation.VizSpecValidator.Validate(request.Spec, model).Errors
            .Where(e => !(e.Code == "unknown_field" && e.Message.Contains($"'{request.FieldName}'", StringComparison.Ordinal)))
            .ToList();
        if (blocking.Count > 0)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, string.Join("; ", blocking.Select(e => $"{e.Path}: {e.Message}")));
        }

        DerivedFieldPlan plan;
        try
        {
            plan = await planner.PlanAsync(new AnalystContext(tenant, version, model), request.FieldName!, request.Hint, cancellationToken);
        }
        catch (DerivedFieldException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
        catch (TokenBudgetExceededException ex)
        {
            return Problem(StatusCodes.Status429TooManyRequests, ex.Message);
        }

        DatasetVersion derived;
        DataThread thread;
        ThreadNode node;
        var finalSpec = request.Spec with { DatasetVersionId = Guid.Empty };
        try
        {
            var id = DatasetVersion.NewId();
            await using (var parquet = File.OpenRead(plan.Result.ParquetPath!))
            {
                await extracts.SaveAsync(tenant, id, parquet, cancellationToken);
            }

            var now = clock.GetUtcNow();
            var user = caller.UserId ?? "unknown";
            derived = DatasetVersion.CreateDerived(id, [version], plan.Sql, request.Hint, plan.Result.Schema!, plan.Result.RowCount, user, now);
            db.DatasetVersions.Add(derived);
            finalSpec = request.Spec with { DatasetVersionId = derived.Id };

            var (loadedThread, parent) = await LoadOrStartThreadAsync(db, request, version, user, now, cancellationToken);
            if (loadedThread is null)
            {
                return TypedResults.NotFound();
            }

            thread = loadedThread;

            node = ThreadNode.Create(thread, parent, derived, finalSpec, plan.Explanation, user, now);
            db.ThreadNodes.Add(node);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            DuckDbSqlSandbox.Cleanup(plan.Result);
        }

        try
        {
            var outcome = await engine.RunAsync(tenant, finalSpec, ImplicitSemanticModel.For(derived), [derived], cancellationToken);
            var chart = new VizQueryResponse(outcome.Result, outcome.Query.Sql, outcome.FromCache, outcome.Duration.TotalMilliseconds);
            return TypedResults.Ok(new DerivedFieldResponse(plan.Sql, plan.Explanation, plan.Result.Preview!, derived.Id, finalSpec, chart, thread.Id, node.Id));
        }
        catch (VizSpecValidationException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
    }

    /// <summary>Continues a thread from its latest step, or starts a new one rooted at the source version.</summary>
    private static async Task<(DataThread? Thread, ThreadNode? Parent)> LoadOrStartThreadAsync(
        InsightFlowDbContext db, DerivedFieldRequest request, DatasetVersion source, string user, DateTimeOffset now, CancellationToken ct)
    {
        if (request.ThreadId is { } threadId)
        {
            var existing = await db.DataThreads.FindAsync([threadId], ct);
            if (existing is null)
            {
                return (null, null);
            }

            var last = await db.ThreadNodes.Where(n => n.ThreadId == threadId).OrderByDescending(n => n.CreatedAt).FirstOrDefaultAsync(ct);
            return (existing, last);
        }

        var title = request.Hint.Length > DataThread.MaxTitleLength ? request.Hint[..DataThread.MaxTitleLength] : request.Hint;
        var thread = DataThread.Start(source.TenantId, title, source, user, now);
        var root = ThreadNode.Create(thread, null, source, null, null, user, now);
        db.DataThreads.Add(thread);
        db.ThreadNodes.Add(root);
        return (thread, root);
    }

    private static async Task<SemanticModel> ResolveModelAsync(InsightFlowDbContext db, Guid? modelId, DatasetVersion version, CancellationToken ct)
    {
        if (modelId is { } id && await db.SemanticModels.FindAsync([id], ct) is { } record)
        {
            return record.ToDomain();
        }

        var models = await db.SemanticModels.AsNoTracking().ToListAsync(ct);
        return models.Select(m => m.ToDomain()).FirstOrDefault(m => m.Uses(version.Id)) ?? ImplicitSemanticModel.For(version);
    }

    private static async IAsyncEnumerable<SseItem<AgentEvent>> ToSse(
        IAsyncEnumerable<AgentEvent> events, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in events.WithCancellation(ct))
        {
            yield return new SseItem<AgentEvent>(e, e.Type);
        }
    }

    private static ProblemHttpResult NotConfigured() =>
        TypedResults.Problem("No AI provider is configured for this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

    private static ProblemHttpResult Problem(int status, string detail) => TypedResults.Problem(detail, statusCode: status);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_ ]{0,62}$")]
    private static partial Regex FieldNamePattern();
}
