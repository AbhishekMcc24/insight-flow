using System.Runtime.CompilerServices;
using System.Threading.Channels;
using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Sandbox;
using InsightFlow.Contracts.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace InsightFlow.Agents.Analyst;

/// <summary>
/// The Analyst agent (Microsoft Agent Framework over the routed <see cref="IChatClient"/>): answers questions about one
/// dataset by describing it, running sandboxed DuckDB SQL and proposing validated charts. Output is a stream of
/// <see cref="AgentEvent"/>s (text, tool, sql + preview, chart, done/error) suitable for Server-Sent Events.
/// </summary>
public sealed partial class AnalystAgent(IModelRouter router, ISqlSandbox sandbox, ILogger<AnalystAgent> logger)
{
    public const string AgentName = "insightflow-analyst";

    internal const string Instructions = """
        You are Insight Flow's data analyst. You answer questions about ONE dataset, exposed as the DuckDB table `input`.

        Rules:
        - Write DuckDB SQL only. Never Python or any other language. Only single SELECT statements over `input`; no files,
          no other tables, no settings.
        - Call DescribeModel first. Use only columns that exist; quote identifiers with double quotes.
        - Compute every number you state with RunSql. If a query fails, read the error, fix the SQL and try again.
        - When a chart helps, call ProposeChart once with the final chart.
        - Answer briefly in plain language: the result first, then one sentence on how it was computed.
        - If the question cannot be answered from this dataset, say so.
        """;

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AnalystContext context, string question, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        var tools = new AnalystTools(context, sandbox, channel.Writer);
        var agent = router.GetClient(context.Tenant, AiTask.SqlGeneration)
            .AsAIAgent(instructions: Instructions, name: AgentName, tools: [.. tools.AsTools()]);

        var run = Task.Run(() => ProduceAsync(agent, context, question, channel.Writer, cancellationToken), cancellationToken);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }

        await run;
    }

    private async Task ProduceAsync(AIAgent agent, AnalystContext context, string question, ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        using var activity = AgentsTelemetry.ActivitySource.StartActivity("analyst.run");
        activity?.SetTag("insightflow.dataset_version_id", context.Version.Id);
        var usage = new UsageDetails();
        try
        {
            await foreach (var update in agent.RunStreamingAsync(question, cancellationToken: ct))
            {
                foreach (var content in update.Contents.OfType<UsageContent>())
                {
                    usage.Add(content.Details);
                }

                if (!string.IsNullOrEmpty(update.Text))
                {
                    await writer.WriteAsync(new AgentEvent(AgentEventTypes.Text, Text: update.Text), ct);
                }
            }

            await writer.WriteAsync(new AgentEvent(AgentEventTypes.Done, InputTokens: usage.InputTokenCount, OutputTokens: usage.OutputTokenCount), ct);
            LogRunCompleted(logger, context.Version.Id, usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex switch
            {
                TokenBudgetExceededException or AiNotConfiguredException => ex.Message,
                _ => "The analyst could not complete the request.",
            };
            LogRunFailed(logger, context.Version.Id, ex.GetType().Name);
            await writer.WriteAsync(new AgentEvent(AgentEventTypes.Error, Error: message), CancellationToken.None);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analyst run on {DatasetVersionId} completed ({InputTokens} input / {OutputTokens} output tokens)")]
    private static partial void LogRunCompleted(ILogger logger, Guid datasetVersionId, long inputTokens, long outputTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analyst run on {DatasetVersionId} failed ({ErrorType})")]
    private static partial void LogRunFailed(ILogger logger, Guid datasetVersionId, string errorType);
}
