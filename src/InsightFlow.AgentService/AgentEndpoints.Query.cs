using InsightFlow.Agent.Ai;
using InsightFlow.Agent.Knowledge;
using InsightFlow.Agent.Sandbox;
using InsightFlow.Domain.Contracts.Knowledge;
using InsightFlow.Domain.Knowledge;
using InsightFlow.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.HttpResults;

namespace InsightFlow.AgentService;

internal static partial class AgentEndpoints
{
    private const int MaxQueryTables = 10;
    private const int MaxQueryPreviewRows = 500;

    /// <summary>
    /// Step 2 of a question. The table ids come from the graph search, but they are only a selection: names, columns, descriptions and
    /// the allowed joins are all read from the caller's own knowledge base here, so a forged id can only pick among their own tables.
    /// </summary>
    private static async Task<Results<Ok<QueryPlanDto>, ProblemHttpResult>> PlanQueryAsync(
        QueryPlanRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        IModelRouter router,
        GraphQueryPlanner planner,
        ModelPriceBook prices,
        Microsoft.Extensions.Options.IOptions<ConversationOptions> conversation,
        CancellationToken cancellationToken)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var question = request.Question?.Trim() ?? string.Empty;
        if (question.Length is 0 or > 2_000)
        {
            return Problem(StatusCodes.Status400BadRequest, "Ask a question of 1–2,000 characters.");
        }

        var ids = (request.TableIds ?? []).Distinct().Take(MaxQueryTables).ToList();
        if (ids.Count == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "No tables were chosen for the question.");
        }

        if (router.AvailableRoutes.Count == 0)
        {
            return NotConfigured();
        }

        var definitions = await db.LoadDefinitionsAsync(cancellationToken);
        var chosen = definitions.Tables.Where(t => ids.Contains(t.Id)).OrderBy(t => ids.IndexOf(t.Id)).ToList();
        if (chosen.Count == 0)
        {
            return Problem(StatusCodes.Status404NotFound, "None of those tables are in the knowledge base.");
        }

        var aliases = GraphQueryPlanner.BuildAliases([.. chosen.Select(t => t.Name)]);
        var columnsByTable = definitions.Columns.GroupBy(c => c.TableId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ordinal).ToList());
        var tables = chosen
            .Select((t, i) => new QueryTable(
                t.Id,
                aliases[i],
                t.DisplayName,
                t.Description,
                [.. columnsByTable.GetValueOrDefault(t.Id, []).Select(c => new QueryColumn(c.Name, c.DataType, c.Description, c.IsPrimaryKey))]))
            .ToList();

        var tableOf = definitions.Columns.ToDictionary(c => c.Id, c => c);
        var joins = definitions.Relationships
            .Where(r => tableOf.ContainsKey(r.FromColumnId) && tableOf.ContainsKey(r.ToColumnId))
            .Select(r => (R: r, From: tableOf[r.FromColumnId], To: tableOf[r.ToColumnId]))
            .Where(x => x.From.TableId != x.To.TableId && chosen.Any(t => t.Id == x.From.TableId) && chosen.Any(t => t.Id == x.To.TableId))
            .Select(x => new QueryJoin(x.From.TableId, x.From.Name, x.To.TableId, x.To.Name, x.R.Kind.ToString(), x.R.Confidence, x.R.GroupId))
            .ToList();

        var guidance = await LoadGuidanceAsync(db, question, request.SkillIds, cancellationToken);
        try
        {
            var plan = await planner.PlanAsync(
                tenant.TenantId!.Value,
                question,
                tables,
                joins,
                ToTurns(request.History, conversation.Value),
                cancellationToken,
                guidance.Guidance);

            var names = tables.ToDictionary(t => t.Id, t => t);
            return TypedResults.Ok(new QueryPlanDto(
                plan.Explanation,
                plan.Sql,
                [.. (plan.UsedTables is { Count: > 0 } used ? used : tables).Select(t => new QueryAliasDto(t.Id, t.Alias, t.Name))],
                [.. plan.UsedJoins.Select(j => new QueryJoinDto(names[j.FromTableId].Name, j.FromColumn, names[j.ToTableId].Name, j.ToColumn, j.Kind, j.Confidence))],
                plan.Assumptions,
                plan.Clarification,
                ToUsageDto(plan.Usage ?? AiUsage.None, prices),
                timer.Elapsed.TotalMilliseconds,
                guidance.Names,
                plan.Chart));
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

    private static readonly System.Text.RegularExpressions.Regex SafeAlias = new("^[a-z_][a-z0-9_]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Step 4 and 5: run the planned SQL over the copies of the tables it uses. The tables are the caller's own knowledge-base tables
    /// (each must already have been copied); the SQL is checked again by the sandbox's guard (one SELECT, only those tables, no files or
    /// network) and runs under a time and row limit. Nothing here changes the source database.
    /// </summary>
    private static async Task<Results<Ok<QueryRunDto>, ProblemHttpResult>> RunQueryAsync(
        QueryRunRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        ISqlSandbox sandbox,
        CancellationToken cancellationToken)
    {
        var sql = request.Sql?.Trim() ?? string.Empty;
        var aliases = request.Tables ?? [];
        if (sql.Length is 0 or > SqlGuard.MaxSqlLength || aliases.Count is 0 or > MaxQueryTables
            || aliases.Any(a => !SafeAlias.IsMatch(a.Alias ?? string.Empty)) || aliases.Select(a => a.Alias).Distinct().Count() != aliases.Count)
        {
            return Problem(StatusCodes.Status400BadRequest, "The query or its tables are not valid.");
        }

        var tableIds = aliases.Select(a => a.Id).Distinct().ToList();
        var tables = await db.KnowledgeTables.Where(t => tableIds.Contains(t.Id)).ToListAsync(cancellationToken);
        if (tables.Count != tableIds.Count)
        {
            return Problem(StatusCodes.Status404NotFound, "One of the tables is not in the knowledge base.");
        }

        var baseline = tables.Any(t => t.Source == DefinitionSource.Dataset) ? await db.LoadDatasetBaselineAsync(cancellationToken) : null;
        var versionIds = new List<Guid>();
        foreach (var alias in aliases)
        {
            var table = tables.Single(t => t.Id == alias.Id);
            var versionId = table.Source == DefinitionSource.Dataset
                ? baseline!.Tables.FirstOrDefault(t => string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase))?.Id
                : table.ExtractVersionId;
            if (versionId is null)
            {
                return Problem(StatusCodes.Status409Conflict, $"“{table.DisplayName}” has not been copied yet. Prepare its data first.");
            }

            versionIds.Add(versionId.Value);
        }

        var versions = await db.DatasetVersions.Where(v => versionIds.Contains(v.Id)).ToListAsync(cancellationToken);
        var inputs = versionIds.Select(id => versions.FirstOrDefault(v => v.Id == id)).ToList();
        if (inputs.Any(v => v is null))
        {
            return Problem(StatusCodes.Status409Conflict, "A table's copy is no longer available. Prepare its data again.");
        }

        var result = await sandbox.RunAsync(
            new SandboxRequest(tenant.TenantId!.Value, [.. inputs.Select(v => v!)], sql, PreviewRows: MaxQueryPreviewRows, InputNames: [.. aliases.Select(a => a.Alias)]),
            cancellationToken);

        return result.Status switch
        {
            SandboxStatus.Succeeded or SandboxStatus.TooManyRows => TypedResults.Ok(new QueryRunDto(result.Preview, result.RowCount, result.Duration.TotalMilliseconds, null, result.Status == SandboxStatus.TooManyRows)),
            _ => TypedResults.Ok(new QueryRunDto(null, 0, result.Duration.TotalMilliseconds, result.Error ?? "The query could not be run.", false)),
        };
    }

    private const int ProceduresPerTable = 3;
    private const int EvidenceCharsPerProcedure = 1_500;

    /// <summary>
    /// How the database itself uses each table, from the stored procedures read earlier: for every table, up to three procedures that
    /// use it (the most focused ones first), cut to the statements that touch that table. Read from the caller's own stored scan, never
    /// from the request.
    /// </summary>
    private static async Task<IReadOnlyList<ProcedureEvidence>> LoadProcedureEvidenceAsync(
        InsightFlowDbContext db, KnowledgeDefinitions definitions, KnowledgeGraphPlan plan, CancellationToken cancellationToken)
    {
        var procedures = await db.KnowledgeProcedures.AsNoTracking().ToListAsync(cancellationToken);
        if (procedures.Count == 0)
        {
            return [];
        }

        var names = plan.Tables.ToDictionary(t => t.Id, t => t.Name);
        var evidence = new List<ProcedureEvidence>();
        foreach (var table in definitions.Tables.Where(t => names.ContainsKey(t.Id)))
        {
            var reference = new ProcedureTableRef(table.Id, table.DatabaseName, table.SchemaName, table.Name);
            foreach (var procedure in procedures.Where(p => p.TableIds.Contains(table.Id)).OrderBy(p => p.TableIds.Length).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(ProceduresPerTable))
            {
                var excerpt = ProcedureUsage.Excerpt(procedure.Definition, procedure.DatabaseName, reference, EvidenceCharsPerProcedure);
                if (excerpt.Length > 0)
                {
                    evidence.Add(new ProcedureEvidence(names[table.Id], $"{procedure.DatabaseName}.{procedure.DisplayName}", excerpt));
                }
            }
        }

        return evidence;
    }

    /// <summary>
    /// The person said an answer was wrong and why. The assistant sees the question, the SQL that was produced, the tables and output
    /// columns involved (never row values), their reason, and the stored-procedure evidence, and proposes new descriptions for databases,
    /// tables and columns so the next attempt picks the right tables, columns, filters and joins. Nothing is saved here.
    /// </summary>
    private static async Task<Results<Ok<KnowledgeChatResponse>, ProblemHttpResult>> FixQueryAsync(
        QueryFixRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        IModelRouter router,
        KnowledgeAssistant assistant,
        ModelPriceBook prices,
        CancellationToken cancellationToken)
    {
        var question = request.Question?.Trim() ?? string.Empty;
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (question.Length is 0 or > 2_000 || reason.Length is 0 or > 1_000 || string.IsNullOrWhiteSpace(request.Sql))
        {
            return Problem(StatusCodes.Status400BadRequest, "Give the question, the SQL and what was wrong (up to 1,000 characters).");
        }

        if (router.AvailableRoutes.Count == 0)
        {
            return NotConfigured();
        }

        var definitions = await db.LoadDefinitionsAsync(cancellationToken);
        var plan = definitions.ToPlan();
        if (plan.Tables.Count == 0)
        {
            return Problem(StatusCodes.Status409Conflict, "The knowledge base has no tables yet.");
        }

        var ids = (request.TableIds ?? []).Distinct().ToHashSet();
        var used = string.Join(", ", plan.Tables.Where(t => ids.Contains(t.Id)).Select(t => t.Name));
        var columns = string.Join(", ", (request.ResultColumns ?? []).Take(20));

        // One chat message must stay under the assistant's per-turn limit, so each part is cut.
        static string Cut(string text, int max) => text.Length > max ? text[..max] + "…" : text;
        var situation =
            $"A question was answered WRONGLY. Question: \"{Cut(question, 250)}\"\n" +
            $"The SQL that was produced: {Cut(request.Sql.Trim(), 700)}\n" +
            $"Tables used: {Cut(used, 200)}. Result columns: {Cut(columns, 100)}.\n" +
            $"The person says it was wrong because: \"{Cut(reason, 400)}\"";
        const string instruction =
            "Change the descriptions of the databases, tables and columns involved so that the same question is answered correctly next time: " +
            "state clearly which table and columns hold the right thing, the right filters and values, and how tables join. " +
            "Change only what this needs, and keep every other description as it is.";

        try
        {
            var evidence = await LoadProcedureEvidenceAsync(db, definitions, plan, cancellationToken);
            var result = await assistant.RespondAsync(
                tenant.TenantId!.Value, plan, KnowledgeAssistMode.Descriptions, [new AssistantTurn("user", situation), new AssistantTurn("user", instruction)], null, onlyMissing: false, cancellationToken, evidence);
            return TypedResults.Ok(new KnowledgeChatResponse(result.Reply, ToDto(plan, result.Suggestions, onlyMissing: false), ToUsageDto(result.Usage ?? AiUsage.None, prices)));
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
