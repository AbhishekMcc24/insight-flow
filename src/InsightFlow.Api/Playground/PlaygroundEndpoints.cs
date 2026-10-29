using InsightFlow.Domain.Contracts.Playground;
using InsightFlow.Foundation.Security;

namespace InsightFlow.Api.Playground;

/// <summary><c>/api/v1/playground</c>: the caller's own Playground sheets.</summary>
internal static class PlaygroundEndpoints
{
    public static RouteGroupBuilder MapPlaygroundEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanView);

        group.MapGet("/sheets", (PlaygroundService playground, CancellationToken ct) => playground.ListAsync(ct)).WithName("ListPlaygroundSheets");
        group.MapGet("/sheets/{sheetId:guid}", (Guid sheetId, PlaygroundService playground, CancellationToken ct) => playground.GetAsync(sheetId, ct)).WithName("GetPlaygroundSheet");
        group.MapPost("/sheets", async (CreatePlaygroundSheetRequest request, PlaygroundService playground, CancellationToken ct) =>
        {
            var sheet = await playground.CreateAsync(request, ct);
            return TypedResults.Created($"/api/v1/playground/sheets/{sheet.Id}", sheet);
        }).WithName("CreatePlaygroundSheet");
        group.MapPut("/sheets/{sheetId:guid}", (Guid sheetId, SavePlaygroundSheetRequest request, PlaygroundService playground, CancellationToken ct) =>
            playground.SaveAsync(sheetId, request, ct)).WithName("SavePlaygroundSheet");
        group.MapPost("/sheets/{sheetId:guid}/duplicate", async (Guid sheetId, PlaygroundService playground, CancellationToken ct) =>
        {
            var sheet = await playground.DuplicateAsync(sheetId, ct);
            return TypedResults.Created($"/api/v1/playground/sheets/{sheet.Id}", sheet);
        }).WithName("DuplicatePlaygroundSheet");
        group.MapDelete("/sheets/{sheetId:guid}", async (Guid sheetId, PlaygroundService playground, CancellationToken ct) =>
        {
            await playground.DeleteAsync(sheetId, ct);
            return TypedResults.NoContent();
        }).WithName("DeletePlaygroundSheet");
        group.MapPost("/export", (ExportToPlaygroundRequest request, PlaygroundService playground, CancellationToken ct) =>
            playground.ExportAsync(request, ct)).WithName("ExportToPlayground");

        return group;
    }
}
