namespace InsightFlow.Domain.Contracts.Skills;

/// <summary>How a version number is shown to people (1 → 1.0.0, 2 → 1.1.0…), so the browser and the server agree.</summary>
public static class SkillLabels
{
    public static string Of(int number) => InsightFlow.Domain.Skills.SkillVersion.Labelled(number);
}

/// <summary>A category of skills (Invoice, Outstanding…), with how many skills it holds.</summary>
public sealed record SkillCategoryDto(Guid Id, string Name, string? Description, int Sequence, int SkillCount, int EnabledCount);

public sealed record SaveSkillCategoryRequest(string Name, string? Description);

/// <summary>A skill as a card shows it: the active version's headline and whether the skill is on.</summary>
public sealed record SkillSummaryDto(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    string Name,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Tables,
    string Disclosure,
    bool IsEnabled,
    int VersionNumber,
    string VersionLabel,
    int VersionCount,
    DateTimeOffset UpdatedAt);

/// <summary>One saved version in a skill's history.</summary>
public sealed record SkillVersionDto(
    Guid Id, int Number, string Label, string? Note, string? CreatedBy, DateTimeOffset CreatedAt, bool IsActive);

/// <summary>A skill with the active version's full content and its version history (newest first).</summary>
public sealed record SkillDetailDto(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    bool IsEnabled,
    Guid ActiveVersionId,
    string Name,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Tables,
    string? License,
    string? Compatibility,
    string Disclosure,
    string Instructions,
    string VersionLabel,
    IReadOnlyList<SkillVersionDto> Versions);

/// <summary>Create a skill, or save an edit (which becomes a new, active version).</summary>
public sealed record SaveSkillRequest(
    Guid CategoryId,
    string Name,
    string Description,
    IReadOnlyList<string>? Tags,
    string? License,
    string? Compatibility,
    string Disclosure,
    string Instructions,
    string? ChangeNote = null,
    IReadOnlyList<string>? Tables = null);

public sealed record SetSkillEnabledRequest(bool Enabled);


/// <summary>Asks the AI to write or improve skills. <see cref="SkillId"/> limits it to one skill (its edit is proposed, not saved); <see cref="CategoryId"/> to one category.</summary>
public sealed record SkillAssistRequest(
    string? Instruction,
    Guid? SkillId = null,
    Guid? CategoryId = null,
    bool UseProcedures = true,
    IReadOnlyList<InsightFlow.Domain.Contracts.Knowledge.QueryTurnDto>? History = null) : InsightFlow.Domain.Contracts.Agents.IUserText
{
    public IEnumerable<string> UserTexts => string.IsNullOrWhiteSpace(Instruction) ? [] : [Instruction];
}

/// <summary>One proposed skill: a new one, or a new version of <see cref="SkillId"/>. <see cref="NewCategory"/> when the category does not exist yet.</summary>
public sealed record SkillSuggestionDto(
    string Action,
    Guid? SkillId,
    string Category,
    bool NewCategory,
    string Name,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Tables,
    string Disclosure,
    string Instructions,
    string? Reason);

public sealed record SkillAssistResponse(string Reply, IReadOnlyList<SkillSuggestionDto> Suggestions, InsightFlow.Domain.Contracts.Agents.UsageDto Usage);

public sealed record ApplySkillSuggestionsRequest(IReadOnlyList<SkillSuggestionDto> Suggestions);

public sealed record ApplySkillSuggestionsResponse(int Created, int Updated, int CategoriesCreated, IReadOnlyList<string> Notes);

/// <summary>Step 1 of a question: which skills it needs (the agent reads the skill catalog, not the skills' full text).</summary>
public sealed record SkillPickRequest(string Question, IReadOnlyList<InsightFlow.Domain.Contracts.Knowledge.QueryTurnDto>? History = null) : InsightFlow.Domain.Contracts.Agents.IUserText
{
    public IEnumerable<string> UserTexts => [Question, .. (History ?? []).Where(t => t.Role == "user").Select(t => t.Content)];
}

public sealed record PickedSkillDto(Guid Id, string Name, string Category, string Version, string Reason, bool Always);

/// <summary>The skills chosen, the knowledge-base tables they involve (resolved to ids), and what the choice cost.</summary>
public sealed record SkillPickResponse(
    IReadOnlyList<PickedSkillDto> Skills,
    IReadOnlyList<Guid> TableIds,
    IReadOnlyList<string> UnresolvedTables,
    int CatalogSize,
    double DurationMs,
    InsightFlow.Domain.Contracts.Agents.UsageDto Usage);
