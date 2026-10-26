using Microsoft.Extensions.AI;

namespace InsightFlow.Agents.Ai;

/// <summary>A forecast of one model request: the tokens it will send and the tokens it is likely to get back.</summary>
public sealed record PromptSize(long InputTokens, long OutputTokens)
{
    /// <summary>
    /// About 3.5 characters per token: a little pessimistic for English, closer to right for identifiers and SQL, so a forecast errs on the
    /// side of costing more than the bill rather than less.
    /// </summary>
    public const double CharactersPerToken = 3.5;

    public static long TokensOf(int characters) => (long)Math.Ceiling(characters / CharactersPerToken);

    public static long TokensOf(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Sum(m => TokensOf((m.Text?.Length ?? 0) + 8)); // a few tokens of framing per message
    }
}
