using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Connections;

/// <summary>Lifecycle of an <see cref="ExtractRun"/>.</summary>
public enum ExtractRunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// One execution of an <see cref="ExtractDefinition"/>. The table doubles as the work queue between the Api (which
/// enqueues runs) and the Worker (which claims and executes them), and as run history for the UI. Error text is a
/// short, user-safe message — never a stack trace, connection string or data sample.
/// </summary>
public sealed class ExtractRun : ITenantOwned
{
    public const int MaxErrorLength = 1_000;

    private ExtractRun()
    {
        RequestedBy = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid DefinitionId { get; private init; }

    public ExtractRunStatus Status { get; private set; }

    public string RequestedBy { get; private init; }

    public DateTimeOffset RequestedAt { get; private init; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public Guid? ResultVersionId { get; private set; }

    public long? RowCount { get; private set; }

    public string? Error { get; private set; }

    public static ExtractRun Enqueue(ExtractDefinition definition, string requestedBy, DateTimeOffset requestedAt)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        return new ExtractRun
        {
            Id = Guid.CreateVersion7(),
            TenantId = definition.TenantId,
            DefinitionId = definition.Id,
            Status = ExtractRunStatus.Pending,
            RequestedBy = requestedBy,
            RequestedAt = requestedAt,
        };
    }

    public void Start(DateTimeOffset now)
    {
        if (Status != ExtractRunStatus.Pending)
        {
            throw new DomainRuleException("run_not_pending", $"A {Status} run cannot be started.");
        }

        Status = ExtractRunStatus.Running;
        StartedAt = now;
    }

    public void Succeed(Guid versionId, long rowCount, DateTimeOffset now)
    {
        EnsureRunning();
        Status = ExtractRunStatus.Succeeded;
        ResultVersionId = versionId;
        RowCount = rowCount;
        CompletedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        EnsureRunning();
        Status = ExtractRunStatus.Failed;
        Error = string.IsNullOrWhiteSpace(error) ? "The extract failed." : error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        CompletedAt = now;
    }

    private void EnsureRunning()
    {
        if (Status != ExtractRunStatus.Running)
        {
            throw new DomainRuleException("run_not_running", $"A {Status} run cannot be completed.");
        }
    }
}
