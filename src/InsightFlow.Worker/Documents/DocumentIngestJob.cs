using Quartz;

namespace InsightFlow.Worker.Documents;

/// <summary>
/// Quartz job that drains the spreadsheet-preparation queue every few seconds, one execution at a time across all Worker replicas
/// (clustered job store), like the extract queue.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DocumentIngestJob(DocumentIngestProcessor processor) : IJob
{
    public static readonly JobKey Key = new("document-ingest", "documents");

    public const int MaxRunsPerTick = 5;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await processor.ProcessPendingAsync(MaxRunsPerTick, cancellationToken);
    }
}
