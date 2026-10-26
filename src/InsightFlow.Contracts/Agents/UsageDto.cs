namespace InsightFlow.Contracts.Agents;

/// <summary>
/// What the AI calls of a step used: tokens in and out, how many calls, the model, and the estimated cost in US dollars at the configured
/// list prices (null when no price is configured for the model, so nothing is guessed).
/// </summary>
public sealed record UsageDto(long InputTokens, long OutputTokens, int Calls, string? Model, decimal? CostUsd)
{
    public long TotalTokens => InputTokens + OutputTokens;

    public static UsageDto None { get; } = new(0, 0, 0, null, 0m);
}

/// <summary>
/// Asks what an AI request would cost before it is made. <see cref="Kind"/> is <c>knowledge</c> (descriptions or relationships, with
/// <see cref="Mode"/>) or <c>skills</c> (writing skills). <see cref="ExtraChars"/> is the size of the conversation and proposal already on
/// screen, which every follow-up sends again.
/// </summary>
public sealed record AiEstimateRequest(
    string Kind,
    string? Mode = null,
    bool OnlyMissing = false,
    bool UseProcedures = true,
    Guid? SkillId = null,
    Guid? CategoryId = null,
    int ExtraChars = 0);

/// <summary>
/// A forecast of one request: tokens in and out, the estimated cost (null when the model has no price), and whether it is big enough that
/// the person should confirm first (<see cref="IsLarge"/>). <see cref="Summary"/> says in words what is being read.
/// </summary>
public sealed record AiEstimateDto(long InputTokens, long OutputTokens, decimal? CostUsd, string? Model, bool IsLarge, string Summary)
{
    public long TotalTokens => InputTokens + OutputTokens;
}
