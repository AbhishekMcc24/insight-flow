using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Analyst;
using InsightFlow.Agents.Sandbox;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace InsightFlow.Agents.DerivedFields;

/// <summary>A validated derived-field plan: the SQL, its explanation, and the materialized result (Parquet in the sandbox work directory).</summary>
public sealed record DerivedFieldPlan(string Sql, string Explanation, SandboxResult Result);

/// <summary>Thrown when the agent could not produce working SQL for a derived field within the allowed attempts.</summary>
public sealed class DerivedFieldException : Exception
{
    public DerivedFieldException()
    {
    }

    public DerivedFieldException(string message)
        : base(message)
    {
    }

    public DerivedFieldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// "Drop a field that doesn't exist yet and AI derives it": asks the SQL model for a DuckDB SELECT over <c>input</c> that keeps
/// every row and column and adds the new column, then proves it in the sandbox (single SELECT, full materialization,
/// same row count, column present). Failures are fed back to the agent (same session) for up to two repairs.
/// Persisting the result as a child <c>DatasetVersion</c> is the host's job.
/// </summary>
public sealed partial class DerivedFieldPlanner(IModelRouter router, ISqlSandbox sandbox, ILogger<DerivedFieldPlanner> logger)
{
    public const int MaxAttempts = 3;
    public const int SampleRows = 5;

    internal const string Instructions = """
        You add ONE computed column to a dataset using DuckDB SQL only (never Python).
        The data is the DuckDB table `input`. Write a single SELECT that returns every row of `input`, all of its columns,
        plus the new column with exactly the requested name, e.g.  SELECT *, <expression> AS "new_name" FROM input
        Do not filter, aggregate or reorder rows. Window functions are fine.
        You may test queries with RunSql. Reply with ONLY a JSON object, no prose and no code fences:
        {"sql": "<the SELECT>", "explanation": "<one or two sentences for a business user>"}
        """;

    public async Task<DerivedFieldPlan> PlanAsync(AnalystContext context, string fieldName, string hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(hint);
        if (context.Version.Schema.Find(fieldName) is not null)
        {
            throw new DerivedFieldException($"The dataset already has a column named '{fieldName}'.");
        }

        using var activity = AgentsTelemetry.ActivitySource.StartActivity("derived_field.plan");
        var events = System.Threading.Channels.Channel.CreateUnbounded<Contracts.Agents.AgentEvent>();
        var tools = new AnalystTools(context, sandbox, events.Writer);
        var agent = router.GetClient(context.Tenant, AiTask.SqlGeneration)
            .AsAIAgent(instructions: Instructions, name: "insightflow-derived-field", tools: [AIFunctionFactory.Create(tools.RunSql, nameof(AnalystTools.RunSql))]);
        var session = await agent.CreateSessionAsync(cancellationToken);

        var prompt = await BuildPromptAsync(context, fieldName, hint, cancellationToken);
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var response = await agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
            if (!TryParse(response.Text, out var sql, out var explanation))
            {
                lastError = "The reply was not the requested JSON object.";
            }
            else
            {
                var result = await sandbox.RunAsync(
                    new SandboxRequest(context.Tenant, [context.Version], sql, Materialize: true, PreviewRows: 20), cancellationToken);
                lastError = Check(result, context, fieldName);
                if (lastError is null)
                {
                    LogPlanned(logger, context.Version.Id, attempt);
                    return new DerivedFieldPlan(sql, explanation, result);
                }

                DuckDbSqlSandbox.Cleanup(result);
            }

            LogAttemptFailed(logger, context.Version.Id, attempt);
            prompt = $"That did not work: {lastError}\nFix it and reply with ONLY the JSON object.";
        }

        throw new DerivedFieldException($"Could not derive '{fieldName}': {lastError}");
    }

    private static string? Check(SandboxResult result, AnalystContext context, string fieldName)
    {
        if (!result.Success)
        {
            return result.Error ?? result.Status.ToString();
        }

        if (result.Schema?.Find(fieldName) is null)
        {
            return $"The result has no column named \"{fieldName}\".";
        }

        if (result.RowCount != context.Version.RowCount)
        {
            return $"The result has {result.RowCount:N0} rows but the input has {context.Version.RowCount:N0}; keep every row.";
        }

        var missing = context.Version.Schema.Columns.Select(c => c.Name)
            .Except(result.Schema.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase).ToList();
        return missing.Count > 0 ? $"Keep all original columns (missing: {string.Join(", ", missing)})." : null;
    }

    private async Task<string> BuildPromptAsync(AnalystContext context, string fieldName, string hint, CancellationToken ct)
    {
        var sb = new StringBuilder(context.DescribeSchema());
        var sample = await sandbox.RunAsync(new SandboxRequest(context.Tenant, [context.Version], "SELECT * FROM input", PreviewRows: SampleRows), ct);
        if (sample.Preview is { } preview)
        {
            sb.AppendLine("First rows:");
            foreach (var row in preview.Rows)
            {
                sb.AppendLine(string.Join(" | ", row.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))));
            }
        }

        sb.Append("New column name: \"").Append(fieldName).AppendLine("\"");
        sb.Append("How to compute it: ").AppendLine(hint);
        return sb.ToString();
    }

    /// <summary>Extracts {"sql","explanation"} from the reply, tolerating code fences or surrounding prose.</summary>
    internal static bool TryParse(string? text, out string sql, out string explanation)
    {
        sql = string.Empty;
        explanation = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = JsonObject().Match(text);
        if (!match.Success)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(match.Value);
            sql = doc.RootElement.GetProperty("sql").GetString() ?? string.Empty;
            explanation = doc.RootElement.TryGetProperty("explanation", out var e) ? e.GetString() ?? string.Empty : string.Empty;
            return sql.Length > 0;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\{[\s\S]*\}")]
    private static partial Regex JsonObject();

    [LoggerMessage(Level = LogLevel.Information, Message = "Derived field planned on {DatasetVersionId} after {Attempts} attempt(s)")]
    private static partial void LogPlanned(ILogger logger, Guid datasetVersionId, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Derived field attempt {Attempt} on {DatasetVersionId} failed")]
    private static partial void LogAttemptFailed(ILogger logger, Guid datasetVersionId, int attempt);
}
