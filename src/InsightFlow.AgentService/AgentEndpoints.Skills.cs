using System.Diagnostics;
using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Knowledge;
using InsightFlow.Agents.Skills;
using InsightFlow.Contracts.Agents;
using InsightFlow.Contracts.Skills;
using InsightFlow.Domain.Skills;
using InsightFlow.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.AgentService;

internal static partial class AgentEndpoints
{
    private sealed record EnabledSkill(Skill Skill, SkillVersion Version, string Category);

    private static UsageDto ToUsageDto(AiUsage usage, ModelPriceBook prices) =>
        new(usage.InputTokens, usage.OutputTokens, usage.Calls, usage.Model, prices.CostOf(usage));

    /// <summary>The organization's skills that are switched on, each with its active version. Read from the caller's own tenant.</summary>
    private static async Task<List<EnabledSkill>> LoadEnabledSkillsAsync(InsightFlowDbContext db, CancellationToken cancellationToken)
    {
        var skills = await db.Skills.AsNoTracking().Where(s => s.IsEnabled).ToListAsync(cancellationToken);
        if (skills.Count == 0)
        {
            return [];
        }

        var versionIds = skills.Select(s => s.ActiveVersionId).ToList();
        var versions = await db.SkillVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var categories = await db.SkillCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        return [.. skills.Where(s => versions.ContainsKey(s.ActiveVersionId)).Select(s => new EnabledSkill(s, versions[s.ActiveVersionId], categories.GetValueOrDefault(s.CategoryId, string.Empty)))];
    }

    /// <summary>
    /// Step 1 of a question: an agent chooses the skills it needs from the catalog of skill headlines. Skills set to "always" are applied
    /// without asking. The tables the chosen skills are about are resolved to ids, so the next step reads only those tables' descriptions.
    /// </summary>
    private static async Task<Ok<SkillPickResponse>> PickSkillsAsync(
        SkillPickRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        IModelRouter router,
        SkillPicker picker,
        ModelPriceBook prices,
        Microsoft.Extensions.Options.IOptions<ConversationOptions> conversation,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var question = request.Question?.Trim() ?? string.Empty;
        var enabled = await LoadEnabledSkillsAsync(db, cancellationToken);
        var always = enabled.Where(s => s.Version.Disclosure == SkillDisclosure.Always).ToList();
        var optional = enabled.Where(s => s.Version.Disclosure != SkillDisclosure.Always).ToList();

        var picked = new List<(EnabledSkill Skill, string Reason)>();
        var usage = AiUsage.None;
        if (question.Length > 0 && optional.Count > 0 && router.AvailableRoutes.Count > 0)
        {
            try
            {
                var catalog = optional.Select(s => new SkillCatalogEntry(s.Skill.Id, s.Category, s.Version.Name, s.Version.Description, s.Version.Tags, s.Version.Tables)).ToList();
                var pick = await picker.PickAsync(
                    tenant.TenantId!.Value, question, catalog, ToTurns(request.History, conversation.Value), cancellationToken);
                usage = pick.Usage;
                picked.AddRange(pick.Picks.Select(p => (optional.First(s => s.Skill.Id == p.Id), p.Reason)));
            }
            catch (TokenBudgetExceededException)
            {
                // Out of allowance: the question carries on with the always-on skills only and the planner reports the allowance itself.
            }
        }

        var chosen = always.Select(s => (Skill: s, Reason: "Always applied")).Concat(picked).ToList();
        var tableNames = chosen.SelectMany(c => c.Skill.Version.Tables).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (ids, unresolved) = await ResolveTablesAsync(db, tableNames, cancellationToken);

        return TypedResults.Ok(new SkillPickResponse(
            [.. chosen.Select(c => new PickedSkillDto(c.Skill.Skill.Id, c.Skill.Version.Name, c.Skill.Category, c.Skill.Version.Label, c.Reason, c.Skill.Version.Disclosure == SkillDisclosure.Always))],
            ids,
            unresolved,
            enabled.Count,
            clock.Elapsed.TotalMilliseconds,
            ToUsageDto(usage, prices)));
    }

    /// <summary>Knowledge-base tables by the names skills list (the display name or the plain name).</summary>
    private static async Task<(List<Guid> Ids, List<string> Unresolved)> ResolveTablesAsync(InsightFlowDbContext db, List<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return ([], []);
        }

        var tables = await db.KnowledgeTables.AsNoTracking().ToListAsync(cancellationToken);
        var ids = new List<Guid>();
        var unresolved = new List<string>();
        foreach (var name in names)
        {
            var table = tables.FirstOrDefault(t => string.Equals(t.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                ?? tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (table is null)
            {
                unresolved.Add(name);
            }
            else if (!ids.Contains(table.Id))
            {
                ids.Add(table.Id);
            }
        }

        return (ids, unresolved);
    }

    /// <summary>The skills the planner is given: the ones the picker chose (and the always-on ones), or, for a caller that did not pick, a keyword match.</summary>
    private static async Task<(IReadOnlyList<SkillGuidance> Guidance, IReadOnlyList<string> Names)> LoadGuidanceAsync(
        InsightFlowDbContext db, string question, IReadOnlyList<Guid>? pickedIds, CancellationToken cancellationToken)
    {
        var enabled = await LoadEnabledSkillsAsync(db, cancellationToken);
        if (enabled.Count == 0)
        {
            return ([], []);
        }

        SkillGuidance Guide(EnabledSkill s) =>
            new(s.Version.Name, s.Category, s.Version.Description, s.Version.Tags, s.Version.Disclosure == SkillDisclosure.Always, s.Version.Instructions);

        IReadOnlyList<SkillGuidance> guidance;
        if (pickedIds is null)
        {
            guidance = SkillSelector.Choose(question, [.. enabled.Select(Guide)]);
        }
        else
        {
            var wanted = pickedIds.ToHashSet();
            guidance = SkillSelector.Choose(string.Empty, [.. enabled.Where(s => wanted.Contains(s.Skill.Id) || s.Version.Disclosure == SkillDisclosure.Always).Select(s => Guide(s) with { Always = true })]);
        }

        return (guidance, [.. guidance.Select(g => g.Name)]);
    }

    private static async Task<(List<SkillCategory> Categories, Dictionary<Guid, string> Names, List<SkillDraft> Existing)> LoadSkillDraftsAsync(
        InsightFlowDbContext db, CancellationToken cancellationToken)
    {
        var categories = await db.SkillCategories.AsNoTracking().OrderBy(c => c.Sequence).ToListAsync(cancellationToken);
        var skills = await db.Skills.AsNoTracking().ToListAsync(cancellationToken);
        var versionIds = skills.Select(s => s.ActiveVersionId).ToList();
        var versions = await db.SkillVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var names = categories.ToDictionary(c => c.Id, c => c.Name);
        var existing = skills
            .Where(s => versions.ContainsKey(s.ActiveVersionId))
            .Select(s =>
            {
                var v = versions[s.ActiveVersionId];
                return new SkillDraft(s.Id, names.GetValueOrDefault(s.CategoryId, string.Empty), v.Name, v.Description, v.Tags, v.Tables, v.Disclosure.ToString(), v.Instructions);
            })
            .ToList();
        return (categories, names, existing);
    }

    /// <summary>
    /// Forecasts a large AI request before it is made, from the real prompt it would send. The numbers come from the same prompt builders
    /// the request uses, so they follow the knowledge base's size; the cost uses the configured model's price (none means no cost shown).
    /// </summary>
    private static async Task<Results<Ok<AiEstimateDto>, ProblemHttpResult>> EstimateAsync(
        AiEstimateRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        ModelPriceBook prices,
        CancellationToken cancellationToken)
    {
        var definitions = await db.LoadDefinitionsAsync(cancellationToken);
        var plan = definitions.ToPlan();
        if (plan.Tables.Count == 0)
        {
            return Problem(StatusCodes.Status409Conflict, "The knowledge base has no tables yet.");
        }

        var evidence = request.UseProcedures ? await LoadProcedureEvidenceAsync(db, definitions, plan, cancellationToken) : null;
        PromptSize size;
        string summary;
        var procedureNote = evidence is { Count: > 0 } ? $" and {evidence.Count} stored-procedure excerpt{(evidence.Count == 1 ? string.Empty : "s")}" : string.Empty;
        switch (request.Kind?.ToLowerInvariant())
        {
            case "knowledge":
                if (!Enum.TryParse<KnowledgeAssistMode>(request.Mode, ignoreCase: true, out var mode))
                {
                    return Problem(StatusCodes.Status400BadRequest, "The mode must be descriptions or relationships.");
                }

                size = KnowledgeAssistant.EstimateSize(plan, mode, request.OnlyMissing, mode == KnowledgeAssistMode.Descriptions ? evidence : null, request.ExtraChars);
                summary = mode == KnowledgeAssistMode.Descriptions
                    ? $"Writes descriptions for {plan.Tables.Count} table{(plan.Tables.Count == 1 ? string.Empty : "s")} and their columns, reading all of them{procedureNote}."
                    : $"Reads the descriptions of {plan.Tables.Count} table{(plan.Tables.Count == 1 ? string.Empty : "s")} to find relationships.";
                break;
            case "skills":
                var (categories, names, existing) = await LoadSkillDraftsAsync(db, cancellationToken);
                string? focusCategory = request.CategoryId is { } cid && names.TryGetValue(cid, out var focusName) ? focusName : null;
                size = SkillAssistant.EstimateSize(plan, [.. categories.Select(c => c.Name)], existing, focusCategory, request.SkillId, evidence);
                size = size with { InputTokens = size.InputTokens + PromptSize.TokensOf(request.ExtraChars) };
                summary = $"Reads {plan.Tables.Count} table{(plan.Tables.Count == 1 ? string.Empty : "s")} with their descriptions and relationships{procedureNote}, then writes " +
                    (request.SkillId is not null ? "one skill." : focusCategory is not null ? $"skills for “{focusCategory}”." : "skills for every business concept it finds.");
                break;
            default:
                return Problem(StatusCodes.Status400BadRequest, "The kind must be knowledge or skills.");
        }

        var model = prices.ModelFor(tenant.TenantId!.Value.ToString(), AiTask.SqlGeneration);
        var usage = new AiUsage(size.InputTokens, size.OutputTokens, 1, model);
        var cost = prices.CostOf(usage);
        return TypedResults.Ok(new AiEstimateDto(size.InputTokens, size.OutputTokens, cost, model, prices.IsLarge(cost, usage.TotalTokens), summary));
    }

    /// <summary>
    /// AI help for skills (administrators): reads the knowledge base and how the stored procedures use it and proposes skills, or an improved
    /// version of one skill. Nothing is stored here.
    /// </summary>
    private static async Task<Results<Ok<SkillAssistResponse>, ProblemHttpResult>> AssistSkillsAsync(
        SkillAssistRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        IModelRouter router,
        SkillAssistant assistant,
        ModelPriceBook prices,
        CancellationToken cancellationToken)
    {
        var history = request.History ?? [];
        if (history.Count > 40 || history.Any(m => m.Content is { Length: > 4_000 }) || request.Instruction is { Length: > 4_000 })
        {
            return Problem(StatusCodes.Status400BadRequest, "The conversation is too long. Start a new one.");
        }

        var definitions = await db.LoadDefinitionsAsync(cancellationToken);
        var plan = definitions.ToPlan();
        if (plan.Tables.Count == 0)
        {
            return Problem(StatusCodes.Status409Conflict, "The knowledge base has no tables yet. Import and describe tables first.");
        }

        if (router.AvailableRoutes.Count == 0)
        {
            return NotConfigured();
        }

        var (categories, categoryNames, existing) = await LoadSkillDraftsAsync(db, cancellationToken);

        if (request.SkillId is { } sid && existing.All(e => e.Id != sid))
        {
            return Problem(StatusCodes.Status404NotFound, "That skill no longer exists.");
        }

        string? focusCategory = request.CategoryId is { } cid && categoryNames.TryGetValue(cid, out var name) ? name : null;
        try
        {
            var result = await assistant.RespondAsync(
                tenant.TenantId!.Value,
                plan,
                [.. categories.Select(c => c.Name)],
                existing,
                focusCategory,
                request.SkillId,
                [.. history.Select(h => new AssistantTurn(h.Role, h.Content))],
                request.Instruction,
                request.UseProcedures ? await LoadProcedureEvidenceAsync(db, definitions, plan, cancellationToken) : null,
                cancellationToken);

            return TypedResults.Ok(new SkillAssistResponse(
                result.Reply,
                [.. result.Suggestions.Select(s => new SkillSuggestionDto(
                    s.SkillId is null ? "create" : "update", s.SkillId, s.Category, s.NewCategory, s.Name, s.Description, s.Tags, s.Tables, s.Disclosure.ToString(), s.Instructions, s.Reason))],
                ToUsageDto(result.Usage, prices)));
        }
        catch (KnowledgeGraphPlanningException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
        catch (TokenBudgetExceededException ex)
        {
            return Problem(StatusCodes.Status429TooManyRequests, ex.Message);
        }
    }
}
