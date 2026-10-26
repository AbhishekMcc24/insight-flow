using InsightFlow.Agents.Guardrails;
using InsightFlow.Contracts.Agents;

namespace InsightFlow.AgentService;

/// <summary>
/// The front of the AgentService: before any endpoint runs, the text a person typed in the request (<see cref="IUserText"/>) goes through the
/// guardrails. When it is refused the request ends here with a 422 and a message that does not repeat the offending words, so no model is
/// called, no table or skill is read and nothing is charged. Only ids, counts and the category are logged, never the text.
/// </summary>
internal sealed partial class GuardrailFilter(IInputGuard guard, ILogger<GuardrailFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var cancellationToken = context.HttpContext.RequestAborted;
        foreach (var argument in context.Arguments.OfType<IUserText>())
        {
            foreach (var text in argument.UserTexts.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                var verdict = await guard.CheckAsync(text, cancellationToken);
                if (!verdict.IsAllowed)
                {
                    LogBlocked(logger, verdict.Code ?? "unknown", context.HttpContext.Request.Path, text.Length);
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        title: "Blocked by the content guardrails",
                        detail: verdict.Message,
                        extensions: new Dictionary<string, object?> { ["code"] = verdict.Code });
                }
            }
        }

        return await next(context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guardrails blocked a request ({Code}) at {Path}; {Length} characters, no model was called")]
    private static partial void LogBlocked(ILogger logger, string code, PathString path, int length);
}
