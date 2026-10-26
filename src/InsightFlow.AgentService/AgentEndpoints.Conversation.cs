using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Knowledge;
using InsightFlow.Contracts.Knowledge;
using InsightFlow.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace InsightFlow.AgentService;

internal static partial class AgentEndpoints
{
    /// <summary>
    /// The conversation as the model reads it: each turn's text, and, when <c>Conversation:ShareResultRows</c> is on, the short digest of what
    /// an answer returned (so "that payment" can be resolved from an earlier result).
    /// </summary>
    private static IReadOnlyList<QueryTurn> ToTurns(IReadOnlyList<QueryTurnDto>? history, ConversationOptions options) =>
        [.. (history ?? []).Select(h => new QueryTurn(h.Role, options.ShareResultRows && !string.IsNullOrWhiteSpace(h.Data) ? $"{h.Content}\n{h.Data}" : h.Content))];

    /// <summary>
    /// Measures a conversation against the model's context window. Nothing is sent to a model until it reaches the configured share (80%); then an
    /// agent summarizes it and the page carries on from the summary, so a long conversation keeps its context without outgrowing the window.
    /// </summary>
    private static async Task<Ok<ConversationCompactDto>> CompactConversationAsync(
        ConversationCompactRequest request,
        ICurrentTenant tenant,
        IModelRouter router,
        ConversationCompactor compactor,
        ModelPriceBook prices,
        IOptions<ConversationOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var turns = ToTurns(request.Turns, settings);
        var model = prices.ModelFor(tenant.TenantId!.Value.ToString(), AiTask.SqlGeneration);
        var window = prices.ContextWindowOf(model);
        var limit = settings.MaxContextTokens is { } cap ? Math.Min(window, cap) : window;
        var threshold = (long)(limit * settings.CompactAtFraction);
        var tokens = ConversationCompactor.EstimateTokens(turns);

        ConversationCompactDto Not() => new(false, null, tokens, threshold, limit, model, InsightFlow.Contracts.Agents.UsageDto.None);
        if ((!request.Force && tokens < threshold) || turns.Count < 2 || router.AvailableRoutes.Count == 0)
        {
            return TypedResults.Ok(Not());
        }

        try
        {
            var result = await compactor.SummarizeAsync(tenant.TenantId!.Value, turns, cancellationToken);
            return TypedResults.Ok(new ConversationCompactDto(true, result.Summary, tokens, threshold, limit, model, ToUsageDto(result.Usage, prices)));
        }
        catch (TokenBudgetExceededException)
        {
            return TypedResults.Ok(Not());
        }
    }
}
