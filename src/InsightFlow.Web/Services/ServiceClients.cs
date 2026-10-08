using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InsightFlow.Contracts.Agents;
using InsightFlow.Contracts.Query;
using InsightFlow.Contracts.Workspace;

namespace InsightFlow.Web.Services;

/// <summary>Typed client for the Api's <c>/api/v1/workspace</c> endpoints (service discovery name <c>api</c>).</summary>
public sealed class WorkspaceApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    private const string Base = "/api/v1/workspace";

    public Task<WorkspaceRootsResponse> GetRootsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/roots", Json.WorkspaceRootsResponse, ct);

    public Task<FolderContentsResponse> GetFolderAsync(Guid folderId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/folders/{folderId}/children", Json.FolderContentsResponse, ct);

    public Task<IReadOnlyList<DatasetSummaryDto>> ListDatasetsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{Base}/datasets", Json.IReadOnlyListDatasetSummaryDto, ct);

    public Task<FolderDto> CreateFolderAsync(Guid parentId, string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/folders", JsonBody(new CreateFolderRequest(parentId, name), Json.CreateFolderRequest), Json.FolderDto, ct);

    public Task<FolderDto> RenameFolderAsync(Guid folderId, string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"{Base}/folders/{folderId}", JsonBody(new RenameRequest(name), Json.RenameRequest), Json.FolderDto, ct);

    public Task<ContentItemDto> RenameItemAsync(Guid itemId, string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"{Base}/items/{itemId}", JsonBody(new RenameRequest(name), Json.RenameRequest), Json.ContentItemDto, ct);

    public Task MoveAsync(MoveRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/move", JsonBody(request, Json.MoveRequest), ct);

    public Task DeleteFolderAsync(Guid folderId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/folders/{folderId}", content: null, ct);

    public Task DeleteItemAsync(Guid itemId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/items/{itemId}", content: null, ct);

    public Task<ExtractQueuedResponse> CreateDatasetAsync(Guid itemId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/items/{itemId}/dataset", content: null, Json.ExtractQueuedResponse, ct);

    public Task<ExtractRunDto> GetExtractRunAsync(Guid runId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"/api/v1/extract-runs/{runId}", Json.ExtractRunDto, ct);

    /// <summary>Opens a file download (headers read, body streamed). Caller disposes the response.</summary>
    public Task<HttpResponseMessage> OpenDownloadAsync(Guid itemId, CancellationToken ct) =>
        SendRawAsync(HttpMethod.Get, $"{Base}/items/{itemId}/content", content: null, HttpCompletionOption.ResponseHeadersRead, ct);
}

/// <summary>
/// Streams a multipart upload body to the Api without buffering. Uses its own HttpClient without the resilience
/// handler: a streamed request body cannot be replayed, so retries/hedging must not apply.
/// </summary>
public sealed class WorkspaceUploadClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    public async Task<UploadResponse> ForwardAsync(Guid folderId, Stream body, string contentType, long? contentLength, CancellationToken ct)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        content.Headers.ContentLength = contentLength;
        return await SendAsync(HttpMethod.Post, $"/api/v1/workspace/folders/{folderId}/files", content, Json.UploadResponse, ct);
    }
}

/// <summary>Typed client for QueryService (<c>queryservice</c>).</summary>
public sealed class QueryApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    public Task<VizQueryResponse> VizAsync(VizQueryRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/query/viz", JsonBody(request, Json.VizQueryRequest), Json.VizQueryResponse, ct);

    public Task<PreviewResponse> PreviewAsync(Guid datasetVersionId, int limit, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/query/preview", JsonBody(new PreviewRequest(datasetVersionId, limit), Json.PreviewRequest), Json.PreviewResponse, ct);
}

/// <summary>Typed client for AgentService (<c>agentservice</c>); model calls are slow, so its timeouts are longer.</summary>
public sealed class AgentApiClient(HttpClient http, ServiceCallCredentials credentials) : ServiceClientBase(http, credentials)
{
    /// <summary>Asks the analyst a question and yields its server-sent events as they arrive.</summary>
    public async IAsyncEnumerable<AgentEvent> AskAnalystAsync(AnalystRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendRawAsync(
            HttpMethod.Post, "/api/v1/agent/analyst", JsonBody(request, Json.AnalystRequest), HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var parser = SseParser.Create(stream, (_, data) => JsonSerializer.Deserialize(data, Json.AgentEvent));
        await foreach (var item in parser.EnumerateAsync(ct))
        {
            if (item.Data is { } agentEvent)
            {
                yield return agentEvent;
            }
        }
    }

    public Task<DerivedFieldResponse> DerivedFieldAsync(DerivedFieldRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/agent/derived-field", JsonBody(request, Json.DerivedFieldRequest), Json.DerivedFieldResponse, ct);
}
