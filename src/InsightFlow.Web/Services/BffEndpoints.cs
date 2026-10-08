using InsightFlow.Contracts;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace InsightFlow.Web.Services;

/// <summary>
/// Browser-facing endpoints for traffic that must not go over the Blazor circuit (large uploads, downloads). They proxy
/// to the internal Api with the signed-in user's credentials, streaming bodies end to end (nothing is buffered).
/// <para>
/// CSRF: state-changing calls require the custom <see cref="CsrfHeader"/> header and, when present, a same-origin
/// <c>Origin</c>. Browsers cannot attach custom headers cross-origin without a CORS preflight, which this app never
/// allows. (The antiforgery form token is not used because validating it would read the multipart body.)
/// </para>
/// </summary>
internal static class BffEndpoints
{
    public const string CsrfHeader = "X-InsightFlow-Request";

    /// <summary>Upper bound for one proxied upload request; the Api enforces its own (configurable) per-file limits too.</summary>
    private const long MaxUploadBytes = 10L << 30;

    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/bff").RequireAuthorization().DisableAntiforgery();

        group.MapPost("/workspace/folders/{folderId:guid}/files", UploadAsync);
        group.MapGet("/workspace/items/{itemId:guid}/content", DownloadAsync);

        return app;
    }

    private static async Task<IResult> UploadAsync(Guid folderId, HttpContext http, WorkspaceUploadClient uploads, CancellationToken ct)
    {
        if (!IsSameOriginScriptRequest(http.Request))
        {
            return TypedResults.Problem("Cross-site request rejected.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (http.Request.ContentType is not { } contentType
            || !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem("Expected a multipart/form-data body.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = MaxUploadBytes;
        }

        try
        {
            var result = await uploads.ForwardAsync(folderId, http.Request.Body, contentType, http.Request.ContentLength, ct);
            return TypedResults.Json(result, ContractsJsonContext.Default.UploadResponse);
        }
        catch (ServiceCallException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: (int)ex.Status);
        }
    }

    private static async Task<IResult> DownloadAsync(Guid itemId, HttpContext http, WorkspaceApiClient workspace, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await workspace.OpenDownloadAsync(itemId, ct);
        }
        catch (ServiceCallException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: (int)ex.Status);
        }

        // Dispose the upstream response once the download has been written.
        http.Response.RegisterForDispose(response);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        return TypedResults.Stream(stream, contentType, fileName);
    }

    private static bool IsSameOriginScriptRequest(HttpRequest request)
    {
        if (!request.Headers.ContainsKey(CsrfHeader))
        {
            return false;
        }

        var origin = request.Headers[HeaderNames.Origin].ToString();
        return string.IsNullOrEmpty(origin)
               || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                   && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase));
    }
}
