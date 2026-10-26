using InsightFlow.Contracts.Skills;
using InsightFlow.Domain;
using InsightFlow.Domain.Skills;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InsightFlow.Api.Skills;

/// <summary>
/// Organization-level skills: the business logic the AI follows when it writes SQL. Categories group the skills; every edit saves a new
/// version that becomes the active one (earlier versions stay and can be made active again); a skill can be switched off without losing it.
/// All of it is the tenant's own, and only administrators reach it.
/// </summary>
public sealed partial class SkillService(InsightFlowDbContext db, ITenantContext caller, TimeProvider clock, ILogger<SkillService> logger)
{
    private static readonly string[] DefaultCategories = ["Invoice", "Outstanding", "Credit Notes", "Debit Notes"];

    private TenantId Tenant => new(caller.TenantId ?? throw new InvalidOperationException("No tenant in scope."));

    private string? User => caller.UserId;

    // ---------------------------------------------------------------------------------------------
    // Categories
    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SkillCategoryDto>> ListCategoriesAsync(CancellationToken cancellationToken)
    {
        await EnsureDefaultCategoriesAsync(cancellationToken);
        var categories = await db.SkillCategories.AsNoTracking().OrderBy(c => c.Sequence).ThenBy(c => c.Name).ToListAsync(cancellationToken);
        var skills = await db.Skills.AsNoTracking().Select(s => new { s.CategoryId, s.IsEnabled }).ToListAsync(cancellationToken);
        return [.. categories.Select(c => new SkillCategoryDto(
            c.Id, c.Name, c.Description, c.Sequence, skills.Count(s => s.CategoryId == c.Id), skills.Count(s => s.CategoryId == c.Id && s.IsEnabled)))];
    }

    public async Task<SkillCategoryDto> CreateCategoryAsync(SaveSkillCategoryRequest request, CancellationToken cancellationToken)
    {
        await EnsureDefaultCategoriesAsync(cancellationToken);
        var name = request.Name?.Trim() ?? string.Empty;
        if ((await db.SkillCategories.Select(c => c.Name).ToListAsync(cancellationToken)).Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw ApiProblemException.Conflict($"A category named “{name}” already exists.");
        }

        var next = (await db.SkillCategories.MaxAsync(c => (int?)c.Sequence, cancellationToken) ?? 0) + 1;
        var category = SkillCategory.Create(Tenant, name, request.Description, next, clock.GetUtcNow());
        db.SkillCategories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        LogCategorySaved(logger, category.Id);
        return new SkillCategoryDto(category.Id, category.Name, category.Description, category.Sequence, 0, 0);
    }

    public async Task<SkillCategoryDto> UpdateCategoryAsync(Guid categoryId, SaveSkillCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await db.SkillCategories.FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken) ?? throw ApiProblemException.NotFound("The category");
        var name = request.Name?.Trim() ?? string.Empty;
        if ((await db.SkillCategories.Where(c => c.Id != categoryId).Select(c => c.Name).ToListAsync(cancellationToken)).Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw ApiProblemException.Conflict($"A category named “{name}” already exists.");
        }

        category.Update(name, request.Description, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        LogCategorySaved(logger, category.Id);
        var skills = await db.Skills.Where(s => s.CategoryId == categoryId).Select(s => s.IsEnabled).ToListAsync(cancellationToken);
        return new SkillCategoryDto(category.Id, category.Name, category.Description, category.Sequence, skills.Count, skills.Count(e => e));
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await db.SkillCategories.FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken) ?? throw ApiProblemException.NotFound("The category");
        if (await db.Skills.AnyAsync(s => s.CategoryId == categoryId, cancellationToken))
        {
            throw ApiProblemException.Conflict("This category still has skills. Move or delete them first.");
        }

        db.SkillCategories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A new tenant starts with the categories of an invoicing database; they can be renamed, added to and deleted.</summary>
    private async Task EnsureDefaultCategoriesAsync(CancellationToken cancellationToken)
    {
        if (await db.SkillCategories.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow();
        for (var i = 0; i < DefaultCategories.Length; i++)
        {
            db.SkillCategories.Add(SkillCategory.Create(Tenant, DefaultCategories[i], null, i + 1, now));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear(); // another request seeded them first
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Skills
    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SkillSummaryDto>> ListSkillsAsync(CancellationToken cancellationToken)
    {
        var categories = await db.SkillCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var skills = await db.Skills.AsNoTracking().ToListAsync(cancellationToken);
        var activeIds = skills.Select(s => s.ActiveVersionId).ToList();
        var active = await db.SkillVersions.AsNoTracking().Where(v => activeIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        return [.. skills
            .Where(s => active.ContainsKey(s.ActiveVersionId))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => ToSummary(s, active[s.ActiveVersionId], categories.GetValueOrDefault(s.CategoryId, "?")))];
    }

    public async Task<SkillDetailDto> GetSkillAsync(Guid skillId, CancellationToken cancellationToken)
    {
        var skill = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Id == skillId, cancellationToken) ?? throw ApiProblemException.NotFound("The skill");
        return await ToDetailAsync(skill, cancellationToken);
    }

    public async Task<SkillDetailDto> CreateSkillAsync(SaveSkillRequest request, CancellationToken cancellationToken)
    {
        await RequireCategoryAsync(request.CategoryId, cancellationToken);
        var content = ToContent(request);
        await RequireUniqueNameAsync(request.CategoryId, content.Name, exceptSkillId: null, cancellationToken);

        var (skill, first) = Skill.Create(Tenant, request.CategoryId, content, User, clock.GetUtcNow());
        db.Skills.Add(skill);
        db.SkillVersions.Add(first);
        await db.SaveChangesAsync(cancellationToken);
        LogSkillSaved(logger, skill.Id, first.Number);
        return await ToDetailAsync(skill, cancellationToken);
    }

    /// <summary>Saves an edit: a new version, active straight away. Moving a skill to another category is part of the edit.</summary>
    public async Task<SkillDetailDto> SaveSkillAsync(Guid skillId, SaveSkillRequest request, CancellationToken cancellationToken)
    {
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == skillId, cancellationToken) ?? throw ApiProblemException.NotFound("The skill");
        await RequireCategoryAsync(request.CategoryId, cancellationToken);
        var content = ToContent(request);
        await RequireUniqueNameAsync(request.CategoryId, content.Name, skill.Id, cancellationToken);

        if (skill.CategoryId != request.CategoryId)
        {
            skill.MoveTo(request.CategoryId, clock.GetUtcNow());
        }

        var version = skill.AddVersion(content, User, request.ChangeNote, clock.GetUtcNow());
        db.SkillVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        LogSkillSaved(logger, skill.Id, version.Number);
        return await ToDetailAsync(skill, cancellationToken);
    }

    public async Task<SkillDetailDto> SetEnabledAsync(Guid skillId, bool enabled, CancellationToken cancellationToken)
    {
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == skillId, cancellationToken) ?? throw ApiProblemException.NotFound("The skill");
        skill.SetEnabled(enabled, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(skill, cancellationToken);
    }

    public async Task<SkillDetailDto> ActivateVersionAsync(Guid skillId, Guid versionId, CancellationToken cancellationToken)
    {
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == skillId, cancellationToken) ?? throw ApiProblemException.NotFound("The skill");
        var version = await db.SkillVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.SkillId == skillId, cancellationToken) ?? throw ApiProblemException.NotFound("The version");
        skill.Activate(version, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        LogSkillSaved(logger, skill.Id, version.Number);
        return await ToDetailAsync(skill, cancellationToken);
    }

    /// <summary>A copy of the skill (its active version) as a new skill, switched off so it can be reviewed first.</summary>
    public async Task<SkillDetailDto> DuplicateAsync(Guid skillId, CancellationToken cancellationToken)
    {
        var skill = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Id == skillId, cancellationToken) ?? throw ApiProblemException.NotFound("The skill");
        var version = await db.SkillVersions.AsNoTracking().FirstAsync(v => v.Id == skill.ActiveVersionId, cancellationToken);
        var taken = await db.Skills.Where(s => s.CategoryId == skill.CategoryId).Select(s => s.Name).ToListAsync(cancellationToken);
        var name = $"{version.Name} (copy)";
        for (var i = 2; taken.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
        {
            name = $"{version.Name} (copy {i})";
        }

        var content = new SkillContent(name, version.Description, version.Tags, version.License, version.Compatibility, version.Disclosure, version.Instructions, version.Tables);
        var (copy, first) = Skill.Create(Tenant, skill.CategoryId, content, User, clock.GetUtcNow());
        copy.SetEnabled(false, clock.GetUtcNow());
        db.Skills.Add(copy);
        db.SkillVersions.Add(first);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(copy, cancellationToken);
    }

    /// <summary>
    /// Stores the skills a person accepted from the AI: new skills (and any new categories they need) and new versions of existing ones.
    /// A suggestion that no longer fits (the skill was deleted, the name is taken) is skipped and reported, never forced.
    /// </summary>
    public async Task<ApplySkillSuggestionsResponse> ApplySuggestionsAsync(ApplySkillSuggestionsRequest request, CancellationToken cancellationToken)
    {
        await EnsureDefaultCategoriesAsync(cancellationToken);
        var suggestions = (request.Suggestions ?? []).Take(20).ToList();
        var notes = new List<string>();
        int created = 0, updated = 0, categoriesCreated = 0;
        var now = clock.GetUtcNow();
        var categories = await db.SkillCategories.ToListAsync(cancellationToken);
        var nextSequence = (categories.Count == 0 ? 0 : categories.Max(c => c.Sequence)) + 1;

        foreach (var s in suggestions)
        {
            try
            {
                var category = categories.FirstOrDefault(c => string.Equals(c.Name, s.Category?.Trim(), StringComparison.OrdinalIgnoreCase));
                var isNewCategory = category is null;
                if (category is null)
                {
                    category = SkillCategory.Create(Tenant, s.Category ?? string.Empty, null, nextSequence++, now);
                    db.SkillCategories.Add(category);
                    categories.Add(category);
                }

                var content = new SkillContent(
                    s.Name ?? string.Empty, s.Description ?? string.Empty, s.Tags ?? [], null, null,
                    Enum.TryParse<SkillDisclosure>(s.Disclosure, ignoreCase: true, out var d) ? d : SkillDisclosure.WhenRelevant, s.Instructions ?? string.Empty, s.Tables ?? []);

                if (s.SkillId is { } id)
                {
                    var skill = await db.Skills.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                    if (skill is null)
                    {
                        notes.Add($"“{s.Name}” was skipped: the skill it improves no longer exists.");
                        continue;
                    }

                    await RequireUniqueNameAsync(category.Id, content.Name, skill.Id, cancellationToken);
                    if (skill.CategoryId != category.Id)
                    {
                        skill.MoveTo(category.Id, now);
                    }

                    db.SkillVersions.Add(skill.AddVersion(content, User, "Improved with AI", now));
                    updated++;
                }
                else
                {
                    await RequireUniqueNameAsync(category.Id, content.Name, null, cancellationToken);
                    var (skill, first) = Skill.Create(Tenant, category.Id, content, User, now);
                    db.Skills.Add(skill);
                    db.SkillVersions.Add(first);
                    created++;
                }

                await db.SaveChangesAsync(cancellationToken);
                if (isNewCategory)
                {
                    categoriesCreated++;
                }
            }
            catch (Exception ex) when (ex is ApiProblemException or DomainRuleException)
            {
                notes.Add($"“{s.Name}” was skipped: {ex.Message}");
                db.ChangeTracker.Clear();
                categories = await db.SkillCategories.ToListAsync(cancellationToken);
            }
        }

        LogApplied(logger, created, updated);
        return new ApplySkillSuggestionsResponse(created, updated, categoriesCreated, notes);
    }

    public async Task DeleteSkillAsync(Guid skillId, CancellationToken cancellationToken)
    {
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == skillId, cancellationToken) ?? throw ApiProblemException.NotFound("The skill");
        db.SkillVersions.RemoveRange(await db.SkillVersions.Where(v => v.SkillId == skillId).ToListAsync(cancellationToken));
        db.Skills.Remove(skill);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------

    private async Task RequireCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        if (!await db.SkillCategories.AnyAsync(c => c.Id == categoryId, cancellationToken))
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "category_not_found", "Choose a category for the skill.");
        }
    }

    private async Task RequireUniqueNameAsync(Guid categoryId, string name, Guid? exceptSkillId, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        if ((await db.Skills.Where(s => s.CategoryId == categoryId && s.Id != exceptSkillId).Select(s => s.Name).ToListAsync(cancellationToken)).Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            throw ApiProblemException.Conflict($"This category already has a skill named “{trimmed}”.");
        }
    }

    private static SkillContent ToContent(SaveSkillRequest request) => new(
        request.Name ?? string.Empty,
        request.Description ?? string.Empty,
        request.Tags ?? [],
        request.License,
        request.Compatibility,
        Enum.TryParse<SkillDisclosure>(request.Disclosure, ignoreCase: true, out var disclosure) ? disclosure : SkillDisclosure.Always,
        request.Instructions ?? string.Empty,
        request.Tables ?? []);

    private static SkillSummaryDto ToSummary(Skill skill, SkillVersion active, string categoryName) => new(
        skill.Id, skill.CategoryId, categoryName, active.Name, active.Description, active.Tags, active.Tables, active.Disclosure.ToString(), skill.IsEnabled,
        active.Number, active.Label, skill.LatestNumber, skill.UpdatedAt);

    private async Task<SkillDetailDto> ToDetailAsync(Skill skill, CancellationToken cancellationToken)
    {
        var versions = await db.SkillVersions.AsNoTracking().Where(v => v.SkillId == skill.Id).OrderByDescending(v => v.Number).ToListAsync(cancellationToken);
        var active = versions.First(v => v.Id == skill.ActiveVersionId);
        var category = await db.SkillCategories.AsNoTracking().Where(c => c.Id == skill.CategoryId).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken) ?? "?";
        return new SkillDetailDto(
            skill.Id, skill.CategoryId, category, skill.IsEnabled, active.Id, active.Name, active.Description, active.Tags, active.Tables, active.License,
            active.Compatibility, active.Disclosure.ToString(), active.Instructions, active.Label,
            [.. versions.Select(v => new SkillVersionDto(v.Id, v.Number, v.Label, v.Note, v.CreatedBy, v.CreatedAt, v.Id == skill.ActiveVersionId))]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AI skill suggestions applied: {Created} created, {Updated} updated")]
    private static partial void LogApplied(ILogger logger, int created, int updated);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skill category {CategoryId} saved")]
    private static partial void LogCategorySaved(ILogger logger, Guid categoryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skill {SkillId} saved at version {Version}")]
    private static partial void LogSkillSaved(ILogger logger, Guid skillId, int version);
}
