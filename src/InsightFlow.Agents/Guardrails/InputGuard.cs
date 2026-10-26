namespace InsightFlow.Agents.Guardrails;

/// <summary>Why text was refused.</summary>
public enum GuardBlock
{
    None,

    /// <summary>Vulgar, slang or abusive language.</summary>
    Vulgarity,

    /// <summary>An attempt to override or extract the assistant's instructions, or to make it ignore its rules.</summary>
    PromptInjection,
}

/// <summary>
/// The verdict on one piece of text. <see cref="Message"/> is safe to show to the person and never repeats the words that were found;
/// <see cref="Code"/> is a stable name for logs and the browser (<c>vulgarity</c>, <c>prompt_injection</c>).
/// </summary>
public sealed record GuardResult(GuardBlock Block, string? Code, string? Message)
{
    public static GuardResult Allowed { get; } = new(GuardBlock.None, null, null);

    public bool IsAllowed => Block == GuardBlock.None;
}

/// <summary>
/// The guardrail layer at the front of every AI request: text a person typed is checked here first, and when it is refused no model is
/// called and nothing else runs. Checks are local (pattern catalogs); no text leaves the server for this.
/// </summary>
public interface IInputGuard
{
    Task<GuardResult> CheckAsync(string text, CancellationToken cancellationToken);
}
