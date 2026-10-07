using Quartz;

namespace InsightFlow.Worker.Extracts;

/// <summary>
/// Quartz job that drains the extract queue every few seconds. <see cref="DisallowConcurrentExecutionAttribute"/> plus the
/// clustered PostgreSQL job store means one execution at a time across all Worker replicas; the SKIP LOCKED claim keeps
/// it safe to lift that restriction later for parallelism.
/// TODO(dev2): scheduled refreshes — a cron per ExtractDefinition that enqueues an ExtractRun (same processor).
/// </summary>
[DisallowConcurrentExecution]
public sealed class ExtractRefreshJob(ExtractRunProcessor processor) : IJob
{
    public static readonly JobKey Key = new("extract-refresh", "extracts");

    /// <summary>Upper bound per tick so one busy tick never starves the scheduler.</summary>
    public const int MaxRunsPerTick = 10;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await processor.ProcessPendingAsync(MaxRunsPerTick, cancellationToken);
    }
}
