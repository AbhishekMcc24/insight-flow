namespace InsightFlow.Agent.Guardrails;

/// <summary>Settings of the front guardrail layer (section <c>Guardrails</c>).</summary>
public sealed class InputGuardOptions
{
    public const string SectionName = "Guardrails";

    /// <summary>Turns the whole layer on or off. On by default; there is no reason to switch it off outside a test.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Refuse vulgar, slang and abusive language (the package's profanity catalog, English and Hinglish).</summary>
    public bool BlockVulgarity { get; set; } = true;

    /// <summary>Refuse attempts to override the assistant's instructions, jailbreaks and requests for its prompt or secrets.</summary>
    public bool BlockPromptInjection { get; set; } = true;

    /// <summary>Longest text that is checked (longer text is refused: no question needs it).</summary>
    public int MaxLength { get; set; } = 4_000;
}
