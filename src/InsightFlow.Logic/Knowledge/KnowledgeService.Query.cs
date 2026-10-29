using InsightFlow.Infrastructure.Connectors.Knowledge;
using InsightFlow.Domain.Contracts.Knowledge;
using InsightFlow.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Logic.Knowledge;

public sealed partial class KnowledgeService
{
    private const int SeedLimit = 3;
    private const int HitLimit = 12;

    /// <summary>
    /// Step 1 of a plain-English question: ask the knowledge graph which tables it is about and how they join. The words of the
    /// question are matched (as parameters) against database, table and column names and descriptions; the best matches are then
    /// connected over the graph's relationships, adding bridge tables where two matches only meet through a third. Names come from
    /// the tenant's own knowledge base, never from the browser.
    /// </summary>
    public async Task<QueryContextDto> GetQueryContextAsync(QueryContextRequest request, CancellationToken cancellationToken)
    {
        var words = QuestionTokens.Extract(request.Question);
        if (words.Count == 0)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "question_too_short", "Ask a question with a few more words, for example “outstanding amount per customer”.");
        }

        var (config, settings) = await LoadSettingsAsync(requireEnabled: true, cancellationToken);
        await KnowledgeEndpointPolicy.EnsureAllowedAsync(config.Uri, AllowPrivateAddresses, cancellationToken);

        var tables = await db.KnowledgeTables.AsNoTracking().ToListAsync(cancellationToken);
        var known = tables.ToDictionary(t => t.Id, t => t.DisplayName);

        var extras = (request.ExtraTableIds ?? []).Where(known.ContainsKey).Distinct().ToList();

        // When the table-picking agent already chose the tables, they are the seeds; the word search is only the fallback for when it did not.
        var hits = request.ChosenByAgent && extras.Count > 0
            ? []
            : (await gateway.FindTablesAsync(settings, Tenant.Value, words, HitLimit, cancellationToken)).Where(h => known.ContainsKey(h.TableId)).ToList();
        var edges = (await gateway.LoadJoinEdgesAsync(settings, Tenant.Value, cancellationToken)).Where(e => known.ContainsKey(e.FromTable) && known.ContainsKey(e.ToTable)).ToList();

        var fallback = false;
        List<GraphTableHit> seeds;
        if (hits.Count > 0)
        {
            var best = hits.Max(h => h.Score);
            seeds = [.. hits.Where(h => h.Score >= Math.Max(2, best * 0.6)).Take(SeedLimit)];
        }
        else if (extras.Count > 0)
        {
            // The skills chosen for the question already name the tables it needs.
            seeds = [];
        }
        else
        {
            var summary = await gateway.SummarizeAsync(settings, Tenant.Value, cancellationToken);
            if (summary.Tables == 0)
            {
                throw new ApiProblemException(StatusCodes.Status409Conflict, "graph_empty", "The knowledge graph is empty. Open Knowledge Base Configuration and push it to Neo4j first.");
            }

            // Nothing mentioned the words: offer the best-connected tables so the person can see what exists and rephrase.
            fallback = true;
            var degree = edges.SelectMany(e => new[] { e.FromTable, e.ToTable }).GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => g.Key);
            seeds = [.. degree.Concat(known.Keys).Distinct().Take(3).Select(id => new GraphTableHit(id, known[id], 0, []))];
        }

        foreach (var extra in extras.Where(e => seeds.All(s => s.TableId != e)))
        {
            seeds.Add(new GraphTableHit(extra, known[extra], 0, []));
        }

        var connection = JoinPaths.Connect([.. seeds.Select(s => s.TableId)], edges);
        var score = hits.ToDictionary(h => h.TableId);
        var chosen = connection.Tables.ToHashSet();

        var tableDtos = connection.Tables
            .Select(id => new QueryTableDto(id, known[id], score.TryGetValue(id, out var h) ? h.Score : 0, score.TryGetValue(id, out var m) ? m.MatchedColumns : [], connection.Bridges.Contains(id)))
            .ToList();

        var joins = edges
            .Where(e => chosen.Contains(e.FromTable) && chosen.Contains(e.ToTable) && e.FromTable != e.ToTable)
            .Select(e => new QueryJoinDto(known[e.FromTable], e.FromColumn, known[e.ToTable], e.ToColumn, e.Kind.ToString(), e.Confidence))
            .Distinct()
            .ToList();

        LogQueryContext(logger, words.Count, tableDtos.Count, joins.Count, fallback);
        return new QueryContextDto(tableDtos, joins, words, fallback, [.. connection.Unconnected.Select(id => known[id])]);
    }

    /// <summary>Keeps a person's verdict on an answer (right, or wrong with the reason).</summary>
    public async Task RecordQueryFeedbackAsync(QueryFeedbackRequest request, CancellationToken cancellationToken)
    {
        db.QueryFeedbacks.Add(QueryFeedback.Create(
            Tenant, caller.UserId ?? string.Empty, request.Question, request.Sql, request.Right, request.Reason, clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Query context: {Words} words matched {Tables} tables and {Joins} joins (fallback {Fallback})")]
    private static partial void LogQueryContext(ILogger logger, int words, int tables, int joins, bool fallback);
}
