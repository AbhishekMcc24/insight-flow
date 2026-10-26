using System.Diagnostics;
using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Knowledge;
using InsightFlow.Contracts.Knowledge;
using InsightFlow.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace InsightFlow.AgentService;

internal static partial class AgentEndpoints
{
    private const int MaxCatalogTables = 60;
    private const int NarrowedCatalogTables = 40;

    /// <summary>
    /// Step 2 of a question: an agent chooses the tables it needs from a compact catalog of the knowledge base (and the relationships between
    /// them), knowing which skills were chosen. The tables the chosen skills are about are always included. A large knowledge base is first
    /// narrowed by word matching to the most likely tables, so the catalog the agent reads stays small. The system, not the agent, connects the
    /// chosen tables over the stored relationships afterwards.
    /// </summary>
    private static async Task<Ok<TablePickResponse>> PickTablesAsync(
        TablePickRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        IModelRouter router,
        TablePicker picker,
        ModelPriceBook prices,
        Microsoft.Extensions.Options.IOptions<ConversationOptions> conversation,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var question = request.Question?.Trim() ?? string.Empty;
        var definitions = await db.LoadDefinitionsAsync(cancellationToken);
        var catalogSize = definitions.Tables.Count;
        TablePickResponse Empty(int considered = 0, AiUsage? usage = null, IReadOnlyList<PickedTableDto>? tables = null) =>
            new(tables ?? [], catalogSize, considered, timer.Elapsed.TotalMilliseconds, ToUsageDto(usage ?? AiUsage.None, prices));

        if (question.Length == 0 || catalogSize == 0)
        {
            return TypedResults.Ok(Empty());
        }

        // The tables the chosen skills are about are needed whatever the agent says.
        var enabled = await LoadEnabledSkillsAsync(db, cancellationToken);
        var wanted = (request.SkillIds ?? []).ToHashSet();
        var skills = enabled.Where(s => wanted.Contains(s.Skill.Id) || s.Version.Disclosure == Domain.Skills.SkillDisclosure.Always).ToList();
        var (skillTableIds, _) = await ResolveTablesAsync(db, [.. skills.SelectMany(s => s.Version.Tables).Distinct(StringComparer.OrdinalIgnoreCase)], cancellationToken);
        var byId = definitions.Tables.ToDictionary(t => t.Id);
        var fromSkills = skillTableIds.Where(byId.ContainsKey)
            .Select(id => new PickedTableDto(id, byId[id].DisplayName, "Named by a chosen skill", true))
            .ToList();

        if (router.AvailableRoutes.Count == 0)
        {
            return TypedResults.Ok(Empty(tables: fromSkills));
        }

        var columns = definitions.Columns.GroupBy(c => c.TableId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ordinal).Select(c => c.Name).ToList());
        var all = definitions.Tables
            .Select(t => new TableCandidate(t.Id, t.DisplayName, t.Description, columns.GetValueOrDefault(t.Id, [])))
            .ToList();
        var candidates = all.Count <= MaxCatalogTables ? all : Narrow(question, all, skillTableIds);

        var ids = candidates.Select(c => c.Id).ToHashSet();
        var tableOfColumn = definitions.Columns.ToDictionary(c => c.Id, c => (c.TableId, c.Name));
        var relations = definitions.Relationships
            .Where(r => tableOfColumn.ContainsKey(r.FromColumnId) && tableOfColumn.ContainsKey(r.ToColumnId))
            .Select(r => (From: tableOfColumn[r.FromColumnId], To: tableOfColumn[r.ToColumnId]))
            .Where(x => x.From.TableId != x.To.TableId && ids.Contains(x.From.TableId) && ids.Contains(x.To.TableId))
            .Select(x => $"{byId[x.From.TableId].DisplayName}.{x.From.Name} -> {byId[x.To.TableId].DisplayName}.{x.To.Name}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var notes = skills
            .Where(s => s.Version.Tables.Length > 0)
            .Select(s => $"{s.Version.Name}: {string.Join(", ", s.Version.Tables)}")
            .ToList();

        try
        {
            var pick = await picker.PickAsync(
                tenant.TenantId!.Value, question, candidates, relations, notes, ToTurns(request.History, conversation.Value), cancellationToken);
            var chosen = fromSkills
                .Concat(pick.Picks.Where(p => fromSkills.All(s => s.Id != p.Id)).Select(p => new PickedTableDto(p.Id, byId[p.Id].DisplayName, p.Reason, false)))
                .ToList();
            return TypedResults.Ok(Empty(candidates.Count, pick.Usage, chosen));
        }
        catch (TokenBudgetExceededException)
        {
            return TypedResults.Ok(Empty(candidates.Count, null, fromSkills));
        }
    }

    /// <summary>The most likely tables for a question by plain word matching, plus the skills' tables: keeps the catalog the agent reads small.</summary>
    private static List<TableCandidate> Narrow(string question, List<TableCandidate> all, IReadOnlyList<Guid> mustKeep)
    {
        var words = question.ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'', '/', '-', '_', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .Distinct()
            .ToList();

        static int Score(TableCandidate t, List<string> words)
        {
            var name = t.Name.ToLowerInvariant();
            var description = (t.Description ?? string.Empty).ToLowerInvariant();
            var columns = string.Join(' ', t.Columns).ToLowerInvariant();
            return words.Sum(w => (name.Contains(w, StringComparison.Ordinal) ? 3 : 0) + (description.Contains(w, StringComparison.Ordinal) ? 2 : 0) + (columns.Contains(w, StringComparison.Ordinal) ? 1 : 0));
        }

        var keep = mustKeep.ToHashSet();
        var top = all.Where(t => !keep.Contains(t.Id)).OrderByDescending(t => Score(t, words)).Take(Math.Max(0, NarrowedCatalogTables - keep.Count));
        return [.. all.Where(t => keep.Contains(t.Id)).Concat(top)];
    }
}
