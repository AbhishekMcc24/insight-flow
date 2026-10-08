using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using InsightFlow.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace InsightFlow.Web.Services;

/// <summary>A service call failed. <see cref="Exception.Message"/> is the service's ProblemDetails title/detail, safe to show to the user.</summary>
public sealed class ServiceCallException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Shared plumbing of the typed clients: user credentials on every request and ProblemDetails → exception.</summary>
public abstract class ServiceClientBase(HttpClient http, ServiceCallCredentials credentials)
{
    protected HttpClient Http { get; } = http;

    protected static ContractsJsonContext Json => ContractsJsonContext.Default;

    protected async Task<T> SendAsync<T>(HttpMethod method, string uri, JsonTypeInfo<T> responseType, CancellationToken ct) =>
        await SendAsync(method, uri, content: null, responseType, ct);

    protected async Task<T> SendAsync<T>(HttpMethod method, string uri, HttpContent? content, JsonTypeInfo<T> responseType, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, uri, content, HttpCompletionOption.ResponseContentRead, ct);
        return await response.Content.ReadFromJsonAsync(responseType, ct)
               ?? throw new ServiceCallException(response.StatusCode, "The service returned an empty response.");
    }

    protected async Task SendAsync(HttpMethod method, string uri, HttpContent? content, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, uri, content, HttpCompletionOption.ResponseContentRead, ct);
    }

    /// <summary>Sends with credentials and throws <see cref="ServiceCallException"/> on a non-success status. Caller disposes.</summary>
    protected async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method, string uri, HttpContent? content, HttpCompletionOption completion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        await credentials.ApplyAsync(request, ct);
        var response = await Http.SendAsync(request, completion, ct);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw new ServiceCallException(response.StatusCode, await ReadProblemAsync(response, ct));
        }
    }

    protected static JsonContent JsonBody<T>(T value, JsonTypeInfo<T> type) => JsonContent.Create(value, type);

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ct);
            if (problem is not null)
            {
                return problem.Detail ?? problem.Title ?? response.ReasonPhrase ?? "The request failed.";
            }
        }
        catch (JsonException)
        {
            // Not a ProblemDetails body; fall through to the status text.
        }
        catch (NotSupportedException)
        {
            // Unexpected content type; fall through.
        }

        return $"The request failed ({(int)response.StatusCode} {response.ReasonPhrase}).";
    }
}
