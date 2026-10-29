using InsightFlow.Infrastructure.Connectors;
using InsightFlow.Infrastructure.Connectors.Knowledge;
using InsightFlow.Domain;
using InsightFlow.Repository;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InsightFlow.Api;

/// <summary>
/// Turns expected exceptions into RFC 7807 responses with a <c>code</c> extension; everything else falls through to the
/// default handler (500 without details). Messages are user-safe by construction.
/// </summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = exception switch
        {
            ApiProblemException p => (p.Status, p.Code, p.Message),
            DomainRuleException d => (StatusCodes.Status400BadRequest, d.Code, d.Message),
            ConnectorException c => (StatusCodes.Status422UnprocessableEntity, "connector_error", c.Message),
            KnowledgeGraphException k => (StatusCodes.Status422UnprocessableEntity, "knowledge_graph_error", k.Message),
            TenantIsolationException => (StatusCodes.Status404NotFound, "not_found", "The resource was not found."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (StatusCodes.Status409Conflict, "name_conflict", "An item with that name already exists here."),
            NotImplementedException => (StatusCodes.Status501NotImplemented, "not_implemented", "This feature is not available yet."),
            _ => (0, string.Empty, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = ReasonPhrases(status),
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }

    private static string ReasonPhrases(int status) => Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status);
}
