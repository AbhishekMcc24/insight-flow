using System.Net.ServerSentEvents;
using System.Text.RegularExpressions;
using InsightFlow.Agent.Ai;
using InsightFlow.Agent.Analyst;
using InsightFlow.Agent.DerivedFields;
using InsightFlow.Agent.Knowledge;
using InsightFlow.Domain.Contracts.Knowledge;
using InsightFlow.Agent.Sandbox;
using InsightFlow.Domain.Contracts.Agents;
using InsightFlow.Domain.Contracts.Query;
using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Threads;
using InsightFlow.Repository;
using InsightFlow.Infrastructure.Query;
using InsightFlow.Infrastructure.Query.Compilation;
using InsightFlow.Infrastructure.Query.Modeling;
using InsightFlow.Infrastructure.Query.Storage;
using InsightFlow.Foundation.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.AgentService;

/// <summary>
/// HTTP surface of the agents. Dataset versions and models are loaded through the tenant-filtered DbContext, so agents only
/// ever see the caller's data. The analyst streams Server-Sent Events; the derived-field flow returns the finished result.
/// </summary>
internal static partial class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanExplore);

        // The front of the service: whatever a person typed is checked by the guardrails before any endpoint (and so any model) runs.
        group.AddEndpointFilter<GuardrailFilter>();

        group.MapPost("/guard/check", (GuardCheckRequest request) => TypedResults.Ok(GuardVerdictDto.Ok))
            .WithName("CheckText")
            .WithSummary("Run text through the guardrails and nothing else. Refused text is answered by the front filter with a 422.");

        group.MapPost("/analyst", AnalystAsync)
            .WithName("AskAnalyst")
            .WithSummary("Ask a question about a dataset; streams text, tool, sql (+preview), chart, done/error events (SSE).");

        group.MapPost("/knowledge/suggest", SuggestKnowledgeAsync)
            .WithName("SuggestKnowledge")
            .WithSummary("AI proposes descriptions for the knowledge base tables and columns and relationships between columns. Nothing is stored.")
            .RequireAuthorization(InsightFlowPolicies.CanAdministerTenant);

        group.MapPost("/knowledge/chat", ChatKnowledgeAsync)
            .WithName("ChatKnowledge")
            .WithSummary("Talk to the knowledge-base assistant: it drafts and revises descriptions or relationships. Nothing is stored.")
            .RequireAuthorization(InsightFlowPolicies.CanAdministerTenant);

        group.MapPost("/knowledge/query/fix", FixQueryAsync)
            .WithName("FixQuery")
            .WithSummary("Propose description changes that make a question the person says was answered wrongly come out right next time. Nothing is stored.");

        group.MapPost("/documents/ask", AskDocumentsAsync)
            .WithName("AskDocuments")
            .WithSummary("Answer a question about the prepared spreadsheets: writes one DuckDB SELECT over their tables and runs it in the sandbox.");

        group.MapPost("/knowledge/query/run", RunQueryAsync)
            .WithName("RunQuery")
            .WithSummary("Run the SQL of a plan over the copies of its tables in the locked sandbox (single SELECT, only those tables, time and row limits).");

        group.MapPost("/estimate", EstimateAsync)
            .WithName("EstimateAi")
            .WithSummary("Forecast the tokens and cost of a large AI request (descriptions, relationships or skills) before it is made.")
            .RequireAuthorization(InsightFlowPolicies.CanAdministerTenant);

        group.MapPost("/conversation/compact", CompactConversationAsync)
            .WithName("CompactConversation")
            .WithSummary("Measure a conversation against the model's context window and, at 80% of it, summarize it so the chat can continue.");

        group.MapPost("/tables/pick", PickTablesAsync)
            .WithName("PickTables")
            .WithSummary("Choose the tables a question needs from a compact catalog of the knowledge base. The system connects them over the stored relationships.");

        group.MapPost("/skills/pick", PickSkillsAsync)
            .WithName("PickSkills")
            .WithSummary("Choose the organization skills a question needs from the skill catalog, and the tables they involve.");

        group.MapPost("/skills/assist", AssistSkillsAsync)
            .WithName("AssistSkills")
            .WithSummary("AI drafts or improves skills from the knowledge base and stored procedures. Nothing is stored.")
            .RequireAuthorization(InsightFlowPolicies.CanAdministerTenant);

        group.MapPost("/knowledge/query/plan", PlanQueryAsync)
            .WithName("PlanQuery")
            .WithSummary("Plan a plain-English question over tables the knowledge graph picked: writes one DuckDB SELECT whose joins are graph relationships. Nothing is run.");

        group.MapPost("/derived-field", DerivedFieldAsync)
            .WithName("DeriveField")
            .WithSummary("Derive a field that does not exist yet from a plain-English hint and chart it.");

        return group;
    }

    private static async Task<IResult> AnalystAsync(
        AnalystRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        IModelRouter router,
        AnalystAgent analyst,
        CancellationToken cancellationToken)
    {
        if (router.AvailableRoutes.Count == 0)
        {
            return NotConfigured();
        }

        var version = await db.DatasetVersions.FindAsync([request.DatasetVersionId], cancellationToken);
        if (version is null)
        {
            return TypedResults.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 4_000)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["question"] = ["Ask a question of up to 4,000 characters."] });
        }

        var model = await ResolveModelAsync(db, request.SemanticModelId, version, cancellationToken);
        var context = new AnalystContext(tenant.TenantId!.Value, version, model);
        var events = analyst.RunAsync(context, request.Question, cancellationToken);
        return TypedResults.ServerSentEvents(ToSse(events, cancellationToken));
    }

    private static async Task<Results<Ok<KnowledgeSuggestionsDto>, ProblemHttpResult>> SuggestKnowledgeAsync(
        KnowledgeSuggestRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        IModelRouter router,
        KnowledgeGraphPlanner planner,
        CancellationToken cancellationToken)
    {
        var plan = (await db.LoadDefinitionsAsync(cancellationToken)).ToPlan();
        if (plan.Tables.Count == 0)
        {
            return Problem(StatusCodes.Status409Conflict, "The knowledge base has no tables yet. Import or add tables first.");
        }

        if (router.AvailableRoutes.Count == 0)
        {
            return NotConfigured();
        }

        try
        {
            var suggestions = await planner.SuggestAsync(tenant.TenantId!.Value, plan, request.OnlyMissing, cancellationToken);
            return TypedResults.Ok(ToDto(plan, suggestions, request.OnlyMissing));
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

    private static async Task<Results<Ok<KnowledgeChatResponse>, ProblemHttpResult>> ChatKnowledgeAsync(
        KnowledgeChatRequest request,
        InsightFlowDbContext db,
        ICurrentTenant tenant,
        IModelRouter router,
        KnowledgeAssistant assistant,
        ModelPriceBook prices,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<KnowledgeAssistMode>(request.Mode, ignoreCase: true, out var mode))
        {
            return Problem(StatusCodes.Status400BadRequest, "The mode must be descriptions or relationships.");
        }

        var messages = request.Messages ?? [];
        if (messages.Count > 60 || messages.Any(m => m.Content is { Length: > 4_000 }))
        {
            return Problem(StatusCodes.Status400BadRequest, "The conversation is too long. Start a new one.");
        }

        var definitions = await db.LoadDefinitionsAsync(cancellationToken);
        var plan = definitions.ToPlan();
        if (plan.Tables.Count == 0)
        {
            return Problem(StatusCodes.Status409Conflict, "The knowledge base has no tables yet. Import or add tables first.");
        }

        if (router.AvailableRoutes.Count == 0)
        {
            return NotConfigured();
        }

        // Names by id, from the server's own copy: the browser's proposal is used only to tell the assistant what is on screen.
        var tableNames = plan.Tables.ToDictionary(t => t.Id, t => t.Name);
        var columnNames = plan.Tables.SelectMany(t => t.Columns.Select(c => (c.Id, Table: t.Name, Column: c.Name))).ToDictionary(x => x.Id, x => (x.Table, x.Column));
        var existing = definitions.Relationships
            .Where(r => columnNames.ContainsKey(r.FromColumnId) && columnNames.ContainsKey(r.ToColumnId))
            .ToDictionary(r => r.Id, r => new GraphRelationship(
                columnNames[r.FromColumnId].Table, columnNames[r.FromColumnId].Column, columnNames[r.ToColumnId].Table, columnNames[r.ToColumnId].Column, r.Kind, r.Confidence, r.Reason));

        var current = ToDomain(request.Current, tableNames, columnNames, existing);
        try
        {
            var result = await assistant.RespondAsync(
                tenant.TenantId!.Value,
                plan,
                mode,
                [.. messages.Select(m => new AssistantTurn(m.Role, m.Content))],
                current,
                request.OnlyMissing,
                cancellationToken,
                mode == KnowledgeAssistMode.Descriptions && request.UseProcedures ? await LoadProcedureEvidenceAsync(db, definitions, plan, cancellationToken) : null);

            var dto = ToDto(plan, result.Suggestions, onlyMissing: false);
            var ids = existing.ToDictionary(kv => RelationshipPairKey(kv.Value), kv => kv.Key, StringComparer.OrdinalIgnoreCase);
            var removals = (result.Suggestions.Removals ?? [])
                .Where(r => ids.ContainsKey(RelationshipPairKey(r)))
                .Select(r => new RelationshipRemovalDto(ids[RelationshipPairKey(r)], $"{r.FromTable}.{r.FromColumn}", $"{r.ToTable}.{r.ToColumn}", r.Kind.ToString()))
                .ToList();
            return TypedResults.Ok(new KnowledgeChatResponse(result.Reply, dto with { Removals = removals }, ToUsageDto(result.Usage ?? AiUsage.None, prices)));
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

    private static string RelationshipPairKey(GraphRelationship r)
    {
        var a = $"{r.FromTable}\u001f{r.FromColumn}";
        var b = $"{r.ToTable}\u001f{r.ToColumn}";
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? $"{a}\u001e{b}" : $"{b}\u001e{a}";
    }

    /// <summary>The proposal on screen, as names the model can read. Anything that no longer exists is left out.</summary>
    private static KnowledgeSuggestions? ToDomain(
        KnowledgeSuggestionsDto? dto,
        Dictionary<Guid, string> tableNames,
        Dictionary<Guid, (string Table, string Column)> columnNames,
        Dictionary<Guid, GraphRelationship> existing)
    {
        if (dto is null)
        {
            return null;
        }

        return new KnowledgeSuggestions(
            [.. (dto.TableDescriptions ?? []).Where(d => tableNames.ContainsKey(d.TableId)).Select(d => new TableDescription(tableNames[d.TableId], d.Description))],
            [.. (dto.ColumnDescriptions ?? []).Where(d => columnNames.ContainsKey(d.ColumnId)).Select(d => new ColumnDescription(columnNames[d.ColumnId].Table, columnNames[d.ColumnId].Column, d.Description))],
            [.. (dto.Relationships ?? [])
                .Where(r => columnNames.ContainsKey(r.FromColumnId) && columnNames.ContainsKey(r.ToColumnId)
                    && Enum.TryParse<RelationshipKind>(r.Kind, ignoreCase: true, out _))
                .Select(r => new GraphRelationship(
                    columnNames[r.FromColumnId].Table, columnNames[r.FromColumnId].Column, columnNames[r.ToColumnId].Table, columnNames[r.ToColumnId].Column,
                    Enum.Parse<RelationshipKind>(r.Kind, ignoreCase: true), r.Confidence, r.Reason))],
            0,
            [.. (dto.Removals ?? []).Where(r => existing.ContainsKey(r.RelationshipId)).Select(r => existing[r.RelationshipId])],
            [.. (dto.DatabaseDescriptions ?? []).Select(d => new DatabaseDescription(d.Database, d.Description))]);
    }

    /// <summary>Resolves the AI's table and column names back to ids and keeps only suggestions that are still useful.</summary>
    private static KnowledgeSuggestionsDto ToDto(KnowledgeGraphPlan plan, KnowledgeSuggestions suggestions, bool onlyMissing)
    {
        var tables = plan.Tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        var tableDescriptions = suggestions.TableDescriptions
            .Where(d => tables.TryGetValue(d.Table, out var t) && (!onlyMissing || string.IsNullOrWhiteSpace(t.Description)))
            .GroupBy(d => d.Table, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TableDescriptionSuggestionDto(tables[g.Key].Id, tables[g.Key].Name, g.First().Description))
            .ToList();

        var columnDescriptions = suggestions.ColumnDescriptions
            .Select(d => (Suggestion: d, Table: tables.GetValueOrDefault(d.Table)))
            .Where(x => x.Table is not null)
            .Select(x => (x.Suggestion, x.Table, Column: x.Table!.Columns.FirstOrDefault(c => string.Equals(c.Name, x.Suggestion.Column, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Column is not null && (!onlyMissing || string.IsNullOrWhiteSpace(x.Column.Description)))
            .GroupBy(x => x.Column!.Id)
            .Select(g => new ColumnDescriptionSuggestionDto(g.Key, g.First().Table!.Name, g.First().Column!.Name, g.First().Suggestion.Description))
            .ToList();

        Guid? ColumnId(string table, string column) =>
            tables.GetValueOrDefault(table)?.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))?.Id;

        var relationships = suggestions.Relationships
            .Select(r => (R: r, From: ColumnId(r.FromTable, r.FromColumn), To: ColumnId(r.ToTable, r.ToColumn)))
            .Where(x => x.From is not null && x.To is not null)
            .Select(x => new RelationshipSuggestionDto(
                x.From!.Value, x.To!.Value, $"{x.R.FromTable}.{x.R.FromColumn}", $"{x.R.ToTable}.{x.R.ToColumn}", x.R.Kind.ToString(), x.R.Confidence, x.R.Reason))
            .ToList();

        var databases = (plan.Databases ?? []).ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var databaseDescriptions = (suggestions.DatabaseDescriptions ?? [])
            .Where(d => databases.TryGetValue(d.Database, out var known) && (!onlyMissing || string.IsNullOrWhiteSpace(known.Description)))
            .Select(d => new DatabaseDescriptionSuggestionDto(databases[d.Database].ConnectionId, databases[d.Database].Name, d.Description))
            .ToList();

        return new KnowledgeSuggestionsDto(tableDescriptions, columnDescriptions, relationships, suggestions.DroppedRelationships, null, databaseDescriptions);
    }

    private static async Task<Results<Ok<DerivedFieldResponse>, NotFound, ProblemHttpResult>> DerivedFieldAsync(
        DerivedFieldRequest request,
        InsightFlowDbContext db,
        ICurrentTenant currentTenant,
        ITenantContext caller,
        IModelRouter router,
        DerivedFieldPlanner planner,
        IExtractStore extracts,
        IVizQueryEngine engine,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (router.AvailableRoutes.Count == 0)
        {
            return NotConfigured();
        }

        if (!FieldNamePattern().IsMatch(request.FieldName ?? string.Empty) || string.IsNullOrWhiteSpace(request.Hint) || request.Hint.Length > 2_000)
        {
            return Problem(StatusCodes.Status400BadRequest, "Use a field name of letters, digits, spaces or underscores (max 63) and a hint of up to 2,000 characters.");
        }

        var tenant = currentTenant.TenantId!.Value;
        var version = await db.DatasetVersions.FindAsync([request.Spec.DatasetVersionId], cancellationToken);
        if (version is null)
        {
            return TypedResults.NotFound();
        }

        var model = await ResolveModelAsync(db, null, version, cancellationToken);
        var blocking = Domain.Validation.VizSpecValidator.Validate(request.Spec, model).Errors
            .Where(e => !(e.Code == "unknown_field" && e.Message.Contains($"'{request.FieldName}'", StringComparison.Ordinal)))
            .ToList();
        if (blocking.Count > 0)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, string.Join("; ", blocking.Select(e => $"{e.Path}: {e.Message}")));
        }

        DerivedFieldPlan plan;
        try
        {
            plan = await planner.PlanAsync(new AnalystContext(tenant, version, model), request.FieldName!, request.Hint, cancellationToken);
        }
        catch (DerivedFieldException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
        catch (TokenBudgetExceededException ex)
        {
            return Problem(StatusCodes.Status429TooManyRequests, ex.Message);
        }

        DatasetVersion derived;
        DataThread thread;
        ThreadNode node;
        var finalSpec = request.Spec with { DatasetVersionId = Guid.Empty };
        try
        {
            var id = DatasetVersion.NewId();
            await using (var parquet = File.OpenRead(plan.Result.ParquetPath!))
            {
                await extracts.SaveAsync(tenant, id, parquet, cancellationToken);
            }

            var now = clock.GetUtcNow();
            var user = caller.UserId ?? "unknown";
            derived = DatasetVersion.CreateDerived(id, [version], plan.Sql, request.Hint, plan.Result.Schema!, plan.Result.RowCount, user, now);
            db.DatasetVersions.Add(derived);
            finalSpec = request.Spec with { DatasetVersionId = derived.Id };

            var (loadedThread, parent) = await LoadOrStartThreadAsync(db, request, version, user, now, cancellationToken);
            if (loadedThread is null)
            {
                return TypedResults.NotFound();
            }

            thread = loadedThread;

            node = ThreadNode.Create(thread, parent, derived, finalSpec, plan.Explanation, user, now);
            db.ThreadNodes.Add(node);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            DuckDbSqlSandbox.Cleanup(plan.Result);
        }

        try
        {
            var outcome = await engine.RunAsync(tenant, finalSpec, ImplicitSemanticModel.For(derived), [derived], cancellationToken);
            var chart = new VizQueryResponse(outcome.Result, outcome.Query.Sql, outcome.FromCache, outcome.Duration.TotalMilliseconds);
            return TypedResults.Ok(new DerivedFieldResponse(plan.Sql, plan.Explanation, plan.Result.Preview!, derived.Id, finalSpec, chart, thread.Id, node.Id));
        }
        catch (VizSpecValidationException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
    }

    /// <summary>Continues a thread from its latest step, or starts a new one rooted at the source version.</summary>
    private static async Task<(DataThread? Thread, ThreadNode? Parent)> LoadOrStartThreadAsync(
        InsightFlowDbContext db, DerivedFieldRequest request, DatasetVersion source, string user, DateTimeOffset now, CancellationToken ct)
    {
        if (request.ThreadId is { } threadId)
        {
            var existing = await db.DataThreads.FindAsync([threadId], ct);
            if (existing is null)
            {
                return (null, null);
            }

            var last = await db.ThreadNodes.Where(n => n.ThreadId == threadId).OrderByDescending(n => n.CreatedAt).FirstOrDefaultAsync(ct);
            return (existing, last);
        }

        var title = request.Hint.Length > DataThread.MaxTitleLength ? request.Hint[..DataThread.MaxTitleLength] : request.Hint;
        var thread = DataThread.Start(source.TenantId, title, source, user, now);
        var root = ThreadNode.Create(thread, null, source, null, null, user, now);
        db.DataThreads.Add(thread);
        db.ThreadNodes.Add(root);
        return (thread, root);
    }

    private static async Task<SemanticModel> ResolveModelAsync(InsightFlowDbContext db, Guid? modelId, DatasetVersion version, CancellationToken ct)
    {
        if (modelId is { } id && await db.SemanticModels.FindAsync([id], ct) is { } record)
        {
            return record.ToDomain();
        }

        var models = await db.SemanticModels.AsNoTracking().ToListAsync(ct);
        return models.Select(m => m.ToDomain()).FirstOrDefault(m => m.Uses(version.Id)) ?? ImplicitSemanticModel.For(version);
    }

    private static async IAsyncEnumerable<SseItem<AgentEvent>> ToSse(
        IAsyncEnumerable<AgentEvent> events, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in events.WithCancellation(ct))
        {
            yield return new SseItem<AgentEvent>(e, e.Type);
        }
    }

    private static ProblemHttpResult NotConfigured() =>
        TypedResults.Problem("No AI provider is configured for this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

    private static ProblemHttpResult Problem(int status, string detail) => TypedResults.Problem(detail, statusCode: status);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_ ]{0,62}$")]
    private static partial Regex FieldNamePattern();
}
