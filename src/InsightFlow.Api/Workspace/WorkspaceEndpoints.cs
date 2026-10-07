using InsightFlow.Contracts.Workspace;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace InsightFlow.Api.Workspace;

/// <summary>
/// <c>/api/v1/workspace</c>: the in-app folder tree (Personal + Shared roots), uploads (including whole dropped
/// folders), downloads and "Create dataset". Every endpoint requires a tenant member; folder-level read/write rules
/// are enforced by <see cref="WorkspaceService"/>.
/// </summary>
internal static class WorkspaceEndpoints
{
    /// <summary>Multipart text field that carries the relative path of the next file part (e.g. <c>Q3/eu/sales.csv</c>).</summary>
    public const string PathField = "path";

    public static RouteGroupBuilder MapWorkspaceEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanView);

        group.MapGet("/roots", (WorkspaceService ws, CancellationToken ct) => ws.GetRootsAsync(ct)).WithName("GetWorkspaceRoots");
        group.MapGet("/folders/{folderId:guid}/children", (Guid folderId, WorkspaceService ws, CancellationToken ct) => ws.GetFolderContentsAsync(folderId, ct))
            .WithName("GetFolderContents");

        group.MapPost("/folders", async (CreateFolderRequest request, WorkspaceService ws, CancellationToken ct) =>
        {
            var folder = await ws.CreateFolderAsync(request, ct);
            return TypedResults.Created($"/api/v1/workspace/folders/{folder.Id}/children", folder);
        }).WithName("CreateFolder");

        group.MapPatch("/folders/{folderId:guid}", (Guid folderId, RenameRequest request, WorkspaceService ws, CancellationToken ct) =>
            ws.RenameFolderAsync(folderId, request.Name, ct)).WithName("RenameFolder");

        group.MapPatch("/items/{itemId:guid}", (Guid itemId, RenameRequest request, WorkspaceService ws, CancellationToken ct) =>
            ws.RenameItemAsync(itemId, request.Name, ct)).WithName("RenameItem");

        group.MapPost("/move", async (MoveRequest request, WorkspaceService ws, CancellationToken ct) =>
        {
            await ws.MoveAsync(request, ct);
            return TypedResults.NoContent();
        }).WithName("MoveWorkspaceItems");

        group.MapDelete("/folders/{folderId:guid}", async (Guid folderId, WorkspaceService ws, CancellationToken ct) =>
        {
            await ws.DeleteFolderAsync(folderId, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteFolder");

        group.MapDelete("/items/{itemId:guid}", async (Guid itemId, WorkspaceService ws, CancellationToken ct) =>
        {
            await ws.DeleteItemAsync(itemId, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteItem");

        group.MapPost("/folders/{folderId:guid}/files", UploadAsync)
            .WithName("UploadFiles")
            .WithSummary("Streaming multipart upload. Send a 'path' text field before each file part to keep folder structure.")
            .DisableAntiforgery();

        group.MapGet("/items/{itemId:guid}/content", async (Guid itemId, WorkspaceService ws, CancellationToken ct) =>
        {
            var (content, name, contentType) = await ws.OpenFileAsync(itemId, ct);
            return TypedResults.Stream(content, contentType, fileDownloadName: name);
        }).WithName("DownloadFile");

        group.MapPost("/items/{itemId:guid}/dataset", async (Guid itemId, WorkspaceService ws, CancellationToken ct) =>
        {
            var queued = await ws.QueueDatasetFromFileAsync(itemId, ct);
            return TypedResults.Accepted($"/api/v1/extract-runs/{queued.RunId}", queued);
        }).WithName("CreateDatasetFromFile").RequireAuthorization(InsightFlowPolicies.CanCreate);

        return group;
    }

    private static async Task<Results<Ok<UploadResponse>, ProblemHttpResult>> UploadAsync(
        Guid folderId, HttpContext http, WorkspaceService ws, IOptions<UploadOptions> options, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 } boundary)
        {
            return TypedResults.Problem("Expected a multipart/form-data body.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = options.Value.MaxRequestBytes;
        }

        var session = await ws.BeginUploadAsync(folderId, ct);
        var reader = new MultipartReader(boundary, http.Request.Body);
        var results = new List<UploadFileResult>();
        string? pendingPath = null;

        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
            {
                continue;
            }

            // Note: IsFormDisposition() means "form field without a file name"; file parts must be checked first.
            if (disposition.IsFileDisposition())
            {
                var path = pendingPath ?? disposition.FileNameStar.Value ?? disposition.FileName.Value ?? string.Empty;
                pendingPath = null;
                results.Add(await ws.UploadFileAsync(session, path, section.Body, section.ContentType, ct));
            }
            else if (disposition.IsFormDisposition() && string.Equals(disposition.Name.Value, PathField, StringComparison.OrdinalIgnoreCase))
            {
                pendingPath = await ReadSmallTextAsync(section.Body, ct);
            }
        }

        return TypedResults.Ok(new UploadResponse(results));
    }

    private static async Task<string> ReadSmallTextAsync(Stream body, CancellationToken ct)
    {
        // leaveOpen: disposing the section stream would end the whole multipart body for the following sections.
        using var reader = new StreamReader(body, leaveOpen: true);
        var buffer = new char[4_097];
        var read = await reader.ReadBlockAsync(buffer, ct);
        return read > 4_096 ? string.Empty : new string(buffer, 0, read);
    }
}
