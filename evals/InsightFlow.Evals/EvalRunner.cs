using System.Diagnostics;
using InsightFlow.Agents.Analyst;
using InsightFlow.Agents.Sandbox;
using InsightFlow.Contracts.Agents;
using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Evals;

/// <summary>One question from questions.yaml.</summary>
public sealed class EvalQuestion
{
    public string Id { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public string ExpectedSql { get; set; } = string.Empty;

    public bool Ordered { get; set; }

    public double Tolerance { get; set; } = 0.001;
}

public sealed class EvalQuestionSet
{
    public List<EvalQuestion> Questions { get; set; } = [];
}

/// <summary>Outcome of one question.</summary>
public sealed record EvalResult(
    string Id,
    string Question,
    bool Passed,
    string Reason,
    string? AgentSql,
    long InputTokens,
    long OutputTokens,
    double Seconds);

/// <summary>
/// Runs questions through an answerer (the Analyst agent, or the reference SQL for the self-test), re-executes the
/// answer's SQL in the sandbox and compares result sets with the expected SQL's result.
/// </summary>
public sealed class EvalRunner(ISqlSandbox sandbox, TenantId tenant, DatasetVersion dataset)
{
    /// <summary>Produces the SQL that answers a question, plus token usage. Null SQL means "no answer".</summary>
    public delegate Task<(string? Sql, long InputTokens, long OutputTokens, string? Error)> Answerer(EvalQuestion question, CancellationToken cancellationToken);

    public async Task<EvalResult> RunAsync(EvalQuestion question, Answerer answer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        var started = Stopwatch.GetTimestamp();

        var expected = await ExecuteAsync(question.ExpectedSql, cancellationToken)
            ?? throw new InvalidOperationException($"Reference SQL of {question.Id} failed — fix questions.yaml.");

        var (sql, input, output, error) = await answer(question, cancellationToken);
        if (sql is null)
        {
            return Result(question, false, error ?? "the agent ran no successful SQL", null, input, output, started);
        }

        var actual = await ExecuteAsync(sql, cancellationToken);
        if (actual is null)
        {
            return Result(question, false, "the agent's SQL failed when re-executed", sql, input, output, started);
        }

        var outcome = ResultSetComparer.Compare(expected, actual, question.Ordered, question.Tolerance);
        return Result(question, outcome.Match, outcome.Reason, sql, input, output, started);
    }

    /// <summary>Answers with the analyst agent: the last SQL that ran successfully is its answer.</summary>
    public static Answerer AgentAnswerer(AnalystAgent analyst, AnalystContext context) => async (question, ct) =>
    {
        string? lastSql = null;
        string? error = null;
        long input = 0, output = 0;
        await foreach (var e in analyst.RunAsync(context, question.Question, ct))
        {
            switch (e.Type)
            {
                case AgentEventTypes.Sql when e.Error is null:
                    lastSql = e.Sql;
                    break;
                case AgentEventTypes.Error:
                    error = e.Error;
                    break;
                case AgentEventTypes.Done:
                    input = e.InputTokens ?? 0;
                    output = e.OutputTokens ?? 0;
                    break;
            }
        }

        return (lastSql, input, output, error);
    };

    /// <summary>Self-test answerer: replays the reference SQL (must score 100 %).</summary>
    public static Answerer ReferenceAnswerer() => (question, _) =>
        Task.FromResult<(string?, long, long, string?)>((question.ExpectedSql, 0, 0, null));

    private async Task<QueryResult?> ExecuteAsync(string sql, CancellationToken ct)
    {
        var result = await sandbox.RunAsync(new SandboxRequest(tenant, [dataset], sql, RowCap: 50_000, PreviewRows: 10_000), ct);
        return result.Success ? result.Preview : null;
    }

    private static EvalResult Result(EvalQuestion q, bool passed, string reason, string? sql, long input, long output, long started) =>
        new(q.Id, q.Question, passed, reason, sql, input, output, Stopwatch.GetElapsedTime(started).TotalSeconds);
}
