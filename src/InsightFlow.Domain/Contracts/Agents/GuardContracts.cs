namespace InsightFlow.Domain.Contracts.Agents;

/// <summary>
/// A request that carries text a person typed. The guardrail layer at the front of the AgentService reads <see cref="UserTexts"/> and refuses
/// the request, before any model is called, when the text is vulgar or tries to override the assistant's instructions.
/// </summary>
public interface IUserText
{
    /// <summary>Only what the person wrote in this request, never earlier assistant replies.</summary>
    IEnumerable<string> UserTexts { get; }
}

/// <summary>Checks one piece of text without doing anything else (used as the very first step of a question).</summary>
public sealed record GuardCheckRequest(string Text) : IUserText
{
    public IEnumerable<string> UserTexts => [Text];
}

/// <summary>
/// The verdict of the guardrail layer. <see cref="Category"/> is <c>vulgarity</c> or <c>prompt_injection</c> when blocked; <see cref="Message"/> is
/// safe to show and never repeats the offending words.
/// </summary>
public sealed record GuardVerdictDto(bool Allowed, string? Category, string? Message)
{
    public static GuardVerdictDto Ok { get; } = new(true, null, null);
}
