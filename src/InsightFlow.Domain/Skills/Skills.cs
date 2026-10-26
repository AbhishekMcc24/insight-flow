using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Skills;

/// <summary>When a skill's instructions are put in front of the model.</summary>
public enum SkillDisclosure
{
    /// <summary>Whenever the skill is on, its instructions are in every question's prompt.</summary>
    Always,

    /// <summary>Only for questions whose words match the skill's name, category or tags.</summary>
    WhenRelevant,
}

/// <summary>
/// A group of skills that belong to one part of the business (Invoice, Outstanding, Credit notes…). Categories are organization level: they
/// are shared by everyone in the tenant and only organise the skills.
/// </summary>
public sealed class SkillCategory : ITenantOwned
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 400;

    private SkillCategory()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public int Sequence { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static SkillCategory Create(TenantId tenant, string name, string? description, int sequence, DateTimeOffset now)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A skill category needs a tenant.");
        }

        var category = new SkillCategory { Id = Guid.CreateVersion7(), TenantId = tenant, CreatedAt = now, Sequence = sequence };
        category.Update(name, description, now);
        return category;
    }

    public void Update(string name, string? description, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            throw new DomainRuleException("invalid_category_name", $"A category name must be 1–{MaxNameLength} characters.");
        }

        var text = description?.Trim();
        if (text is { Length: > MaxDescriptionLength })
        {
            throw new DomainRuleException("invalid_category_description", $"A category description can have at most {MaxDescriptionLength} characters.");
        }

        Name = trimmed;
        Description = string.IsNullOrEmpty(text) ? null : text;
        UpdatedAt = now;
    }
}

/// <summary>
/// A piece of business logic the AI follows when it writes SQL (how "outstanding" is worked out, which statuses count as paid…). The skill
/// itself only holds its place (category, on/off) and which version is active; everything a person edits lives in immutable
/// <see cref="SkillVersion"/>s, so every change is kept and any earlier version can be made active again.
/// </summary>
public sealed class Skill : ITenantOwned
{
    private Skill()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid CategoryId { get; private set; }

    /// <summary>The active version's name, kept here so lists and uniqueness checks do not need the versions.</summary>
    public string Name { get; private set; }

    /// <summary>Off skills are never given to the model. Switching a skill off or on does not create a version.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>The version the model uses (the newest one unless someone chose an older one).</summary>
    public Guid ActiveVersionId { get; private set; }

    /// <summary>The highest version number given so far.</summary>
    public int LatestNumber { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static (Skill Skill, SkillVersion First) Create(TenantId tenant, Guid categoryId, SkillContent content, string? createdBy, DateTimeOffset now)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A skill needs a tenant.");
        }

        var skill = new Skill { Id = Guid.CreateVersion7(), TenantId = tenant, CategoryId = categoryId, IsEnabled = true, CreatedAt = now };
        var first = skill.AddVersion(content, createdBy, "Created", now);
        return (skill, first);
    }

    /// <summary>Saves an edit as a new version and makes it the active one.</summary>
    public SkillVersion AddVersion(SkillContent content, string? createdBy, string? note, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        content.Validate();
        LatestNumber++;
        var version = SkillVersion.Create(this, LatestNumber, content, createdBy, note, now);
        ActiveVersionId = version.Id;
        Name = content.Name.Trim();
        UpdatedAt = now;
        return version;
    }

    /// <summary>Makes an earlier version the active one again (a rollback). No new version is created.</summary>
    public void Activate(SkillVersion version, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.SkillId != Id)
        {
            throw new DomainRuleException("version_mismatch", "That version belongs to another skill.");
        }

        ActiveVersionId = version.Id;
        Name = version.Name;
        UpdatedAt = now;
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        IsEnabled = enabled;
        UpdatedAt = now;
    }

    public void MoveTo(Guid categoryId, DateTimeOffset now)
    {
        CategoryId = categoryId;
        UpdatedAt = now;
    }
}

/// <summary>What a person edits for a skill: the text fields of one version.</summary>
public sealed record SkillContent(
    string Name,
    string Description,
    IReadOnlyList<string> Tags,
    string? License,
    string? Compatibility,
    SkillDisclosure Disclosure,
    string Instructions,
    IReadOnlyList<string>? Tables = null)
{
    public void Validate()
    {
        var name = Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > SkillVersion.MaxNameLength)
        {
            throw new DomainRuleException("invalid_skill_name", $"A skill name must be 1–{SkillVersion.MaxNameLength} characters.");
        }

        if ((Description?.Trim() ?? string.Empty).Length is 0 or > SkillVersion.MaxDescriptionLength)
        {
            throw new DomainRuleException("invalid_skill_description", $"The one-line description must be 1–{SkillVersion.MaxDescriptionLength} characters.");
        }

        if ((Instructions?.Trim() ?? string.Empty).Length is 0 or > SkillVersion.MaxInstructionsLength)
        {
            throw new DomainRuleException("invalid_skill_instructions", $"The instructions must be 1–{SkillVersion.MaxInstructionsLength:N0} characters.");
        }

        if ((License?.Length ?? 0) > SkillVersion.MaxShortLength || (Compatibility?.Length ?? 0) > SkillVersion.MaxCompatibilityLength)
        {
            throw new DomainRuleException("invalid_skill_field", "The license or compatibility text is too long.");
        }

        if (!Enum.IsDefined(Disclosure))
        {
            throw new DomainRuleException("invalid_skill_disclosure", "Choose when the skill is shown to the AI.");
        }

        if ((Tables?.Count ?? 0) > SkillVersion.MaxTables || Tables?.Any(t => t.Length > SkillVersion.MaxTableNameLength) == true)
        {
            throw new DomainRuleException("invalid_skill_tables", $"A skill can list at most {SkillVersion.MaxTables} tables.");
        }

        if ((Tags?.Count ?? 0) > SkillVersion.MaxTags || Tags?.Any(t => t.Length > SkillVersion.MaxShortLength) == true)
        {
            throw new DomainRuleException("invalid_skill_tags", $"A skill can have at most {SkillVersion.MaxTags} tags of up to {SkillVersion.MaxShortLength} characters.");
        }
    }

    /// <summary>Splits a comma separated list into clean, distinct tags.</summary>
    public static IReadOnlyList<string> ParseTags(string? text) =>
        [.. (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>One saved state of a skill. Never changed after it is created.</summary>
public sealed class SkillVersion : ITenantOwned
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 300;
    public const int MaxInstructionsLength = 20_000;
    public const int MaxShortLength = 64;
    public const int MaxCompatibilityLength = 300;
    public const int MaxNoteLength = 200;
    public const int MaxTags = 12;
    public const int MaxTables = 30;
    public const int MaxTableNameLength = 300;

    private SkillVersion()
    {
        Name = string.Empty;
        Description = string.Empty;
        Instructions = string.Empty;
        Tags = [];
        Tables = [];
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid SkillId { get; private init; }

    /// <summary>1, 2, 3… in the order the versions were saved.</summary>
    public int Number { get; private init; }

    public string Name { get; private init; }

    public string Description { get; private init; }

    public string[] Tags { get; private init; }

    /// <summary>Names of the knowledge-base tables the skill is about (as the knowledge base shows them). They are what a question using the skill reads, so only these tables' descriptions are sent.</summary>
    public string[] Tables { get; private init; }

    public string? License { get; private init; }

    public string? Compatibility { get; private init; }

    public SkillDisclosure Disclosure { get; private init; }

    /// <summary>The skill's markdown instructions.</summary>
    public string Instructions { get; private init; }

    public string? Note { get; private init; }

    public string? CreatedBy { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>The version as people read it: 1 → 1.0.0, 2 → 1.1.0, 3 → 1.2.0 (every saved edit is a minor step).</summary>
    public string Label => Labelled(Number);

    public static string Labelled(int number) => $"1.{Math.Max(0, number - 1)}.0";

    internal static SkillVersion Create(Skill skill, int number, SkillContent content, string? createdBy, string? note, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = skill.TenantId,
        SkillId = skill.Id,
        Number = number,
        Name = content.Name.Trim(),
        Description = content.Description.Trim(),
        Tags = [.. content.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
        Tables = [.. (content.Tables ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
        License = string.IsNullOrWhiteSpace(content.License) ? null : content.License.Trim(),
        Compatibility = string.IsNullOrWhiteSpace(content.Compatibility) ? null : content.Compatibility.Trim(),
        Disclosure = content.Disclosure,
        Instructions = content.Instructions.Trim(),
        Note = string.IsNullOrWhiteSpace(note) ? null : (note.Trim().Length > MaxNoteLength ? note.Trim()[..MaxNoteLength] : note.Trim()),
        CreatedBy = createdBy,
        CreatedAt = now,
    };
}
