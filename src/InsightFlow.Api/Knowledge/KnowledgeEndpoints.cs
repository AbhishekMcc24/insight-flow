using InsightFlow.Contracts.Knowledge;
using InsightFlow.ServiceDefaults.Security;

namespace InsightFlow.Api.Knowledge;

/// <summary>
/// <c>/api/v1/knowledge</c>: the Neo4j connection, the editable table/column/relationship definitions, and pushing them to Neo4j.
/// Sys-admin only.
/// </summary>
internal static class KnowledgeEndpoints
{
    public static RouteGroupBuilder MapKnowledgeEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanAdministerTenant);

        group.MapGet("/config", (KnowledgeService knowledge, CancellationToken ct) => knowledge.GetConfigAsync(ct)).WithName("GetKnowledgeConfig");
        group.MapPut("/config", (SaveKnowledgeBaseConfigRequest request, KnowledgeService knowledge, CancellationToken ct) =>
            knowledge.SaveConfigAsync(request, ct)).WithName("SaveKnowledgeConfig");
        group.MapPost("/config/test", (KnowledgeService knowledge, CancellationToken ct) => knowledge.TestAsync(ct)).WithName("TestKnowledgeConfig");

        group.MapGet("/definitions", (KnowledgeService knowledge, CancellationToken ct) => knowledge.GetDefinitionsAsync(ct)).WithName("GetKnowledgeDefinitions");
        group.MapPost("/definitions/import", (ImportTablesRequest request, KnowledgeService knowledge, CancellationToken ct) =>
            knowledge.ImportFromConnectionAsync(request, ct)).WithName("ImportKnowledgeTables");
        group.MapPost("/definitions/import-datasets", (KnowledgeService knowledge, CancellationToken ct) =>
            knowledge.ImportDatasetsAsync(ct)).WithName("ImportKnowledgeDatasets");

        group.MapPost("/definitions/tables", async (SaveKnowledgeTableRequest request, KnowledgeService knowledge, CancellationToken ct) =>
        {
            var table = await knowledge.CreateTableAsync(request, ct);
            return TypedResults.Created($"/api/v1/knowledge/definitions/tables/{table.Id}", table);
        }).WithName("CreateKnowledgeTable");
        group.MapPut("/definitions/tables/{tableId:guid}", (Guid tableId, SaveKnowledgeTableRequest request, KnowledgeService knowledge, CancellationToken ct) =>
            knowledge.UpdateTableAsync(tableId, request, ct)).WithName("UpdateKnowledgeTable");
        group.MapPost("/definitions/tables/remove", (RemoveTablesRequest request, KnowledgeService knowledge, CancellationToken ct) =>
            knowledge.RemoveTablesAsync(request.TableIds, ct)).WithName("RemoveKnowledgeTables");
        group.MapDelete("/definitions/tables/{tableId:guid}", async (Guid tableId, KnowledgeService knowledge, CancellationToken ct) =>
        {
            await knowledge.DeleteTableAsync(tableId, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteKnowledgeTable");

        group.MapDelete("/definitions/columns/{columnId:guid}", async (Guid columnId, KnowledgeService knowledge, CancellationToken ct) =>
        {
            await knowledge.RemoveColumnAsync(columnId, ct);
            return TypedResults.NoContent();
        }).WithName("RemoveKnowledgeColumn");
        group.MapPost("/definitions/relationships", (SaveKnowledgeRelationshipRequest request, KnowledgeService knowledge, CancellationToken ct) =>
            knowledge.AddRelationshipAsync(request, ct)).WithName("AddKnowledgeRelationship");
        group.MapPut("/definitions/databases", (SaveDatabaseDescriptionRequest request, KnowledgeService knowledge, CancellationToken ct) =>
            knowledge.SaveDatabaseDescriptionAsync(request, ct)).WithName("SaveKnowledgeDatabaseDescription");
        group.MapGet("/procedures", (KnowledgeSourceService source, CancellationToken ct) => source.GetProceduresSummaryAsync(ct)).WithName("GetKnowledgeProcedures");
        group.MapPost("/procedures/scan", (KnowledgeSourceService source, CancellationToken ct) => source.ScanProceduresAsync(ct)).WithName("ScanKnowledgeProcedures");
        group.MapPost("/definitions/relationships/composite", (SaveCompositeRelationshipRequest request, KnowledgeService knowledge, CancellationToken ct) =>
            knowledge.AddCompositeRelationshipAsync(request, ct)).WithName("AddCompositeKnowledgeRelationship");
        group.MapDelete("/definitions/relationships/{relationshipId:guid}", async (Guid relationshipId, KnowledgeService knowledge, CancellationToken ct) =>
        {
            await knowledge.DeleteRelationshipAsync(relationshipId, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteKnowledgeRelationship");
        group.MapPost("/definitions/suggestions/apply", (ApplySuggestionsRequest request, KnowledgeService knowledge, CancellationToken ct) =>
            knowledge.ApplySuggestionsAsync(request, ct)).WithName("ApplyKnowledgeSuggestions");

        group.MapPost("/source/test", (SourceConnectionRequest request, KnowledgeSourceService source, CancellationToken ct) =>
            source.TestAsync(request, ct)).WithName("TestKnowledgeSource");
        group.MapPost("/source/connections", (SourceConnectionRequest request, KnowledgeSourceService source, CancellationToken ct) =>
            source.SaveAsync(request, ct)).WithName("SaveKnowledgeSource");
        group.MapDelete("/source/connections/{connectionId:guid}", async (Guid connectionId, KnowledgeSourceService source, CancellationToken ct) =>
        {
            await source.DeleteAsync(connectionId, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteKnowledgeSource");
        group.MapGet("/source/connections/{connectionId:guid}/databases", (Guid connectionId, KnowledgeSourceService source, CancellationToken ct) =>
            source.ListDatabasesAsync(connectionId, ct)).WithName("ListKnowledgeSourceDatabases");
        group.MapGet("/source/connections/{connectionId:guid}/tables", (Guid connectionId, string? database, KnowledgeSourceService source, CancellationToken ct) =>
            source.ListTablesAsync(connectionId, database, ct)).WithName("ListKnowledgeSourceTables");
        group.MapPost("/source/connections/{connectionId:guid}/describe", (Guid connectionId, SourceDescribeRequest request, KnowledgeSourceService source, CancellationToken ct) =>
            source.DescribeAsync(connectionId, request, ct)).WithName("DescribeKnowledgeSourceTables");

        group.MapGet("/graph/summary", (KnowledgeService knowledge, CancellationToken ct) => knowledge.GetSummaryAsync(ct)).WithName("GetKnowledgeGraphSummary");
        group.MapPost("/graph/push", (KnowledgeService knowledge, CancellationToken ct) => knowledge.PushAsync(ct)).WithName("PushKnowledgeGraph");

        return group;
    }
}
