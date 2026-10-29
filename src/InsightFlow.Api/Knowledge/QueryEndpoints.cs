using InsightFlow.Domain.Contracts.Knowledge;
using InsightFlow.Foundation.Security;

namespace InsightFlow.Api.Knowledge;

/// <summary>
/// <c>/api/v1/query</c>: asking questions of the knowledge graph. Open to every member of the tenant (viewers included), unlike the
/// configuration endpoints, because a question only reads.
/// </summary>
internal static class QueryEndpoints
{
    public static RouteGroupBuilder MapQueryEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanView);
        group.MapPost("/context", (QueryContextRequest request, KnowledgeService knowledge, CancellationToken ct) =>
            knowledge.GetQueryContextAsync(request, ct)).WithName("GetQueryContext");
        group.MapPost("/feedback", async (QueryFeedbackRequest request, KnowledgeService knowledge, CancellationToken ct) =>
        {
            await knowledge.RecordQueryFeedbackAsync(request, ct);
            return TypedResults.NoContent();
        }).WithName("RecordQueryFeedback").RequireAuthorization(InsightFlowPolicies.CanExplore);
        group.MapPost("/tables/{tableId:guid}/extract", (Guid tableId, QueryExtractRequest request, KnowledgeSourceService source, CancellationToken ct) =>
            source.PrepareQueryTableAsync(tableId, request?.Refresh ?? false, ct))
            .WithName("PrepareQueryTable").RequireAuthorization(InsightFlowPolicies.CanExplore);
        return group;
    }
}
