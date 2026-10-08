using InsightFlow.Contracts.Query;
using InsightFlow.Domain.Viz;

namespace InsightFlow.Contracts.Agents;

/// <summary>Body of <c>POST /api/v1/agent/analyst</c>: a plain-English question about one dataset version.</summary>
public sealed record AnalystRequest(Guid DatasetVersionId, string Question, Guid? SemanticModelId = null);

/// <summary>Event kinds streamed by the analyst (SSE event names).</summary>
public static class AgentEventTypes
{
    /// <summary>A chunk of the agent's answer text.</summary>
    public const string Text = "text";

    /// <summary>The agent started calling a tool (<see cref="AgentEvent.Tool"/>).</summary>
    public const string Tool = "tool";

    /// <summary>SQL ran in the sandbox: <see cref="AgentEvent.Sql"/> plus <see cref="AgentEvent.Preview"/> or <see cref="AgentEvent.Error"/>.</summary>
    public const string Sql = "sql";

    /// <summary>A validated chart proposal (<see cref="AgentEvent.Spec"/>).</summary>
    public const string Chart = "chart";

    public const string Error = "error";

    /// <summary>The run finished; token usage is attached.</summary>
    public const string Done = "done";
}

/// <summary>
/// One streamed analyst event. Every AI answer shows its SQL and a data preview (product rule), so <c>sql</c> events
/// carry both. Fields not relevant to <see cref="Type"/> are null.
/// </summary>
public sealed record AgentEvent(
    string Type,
    string? Text = null,
    string? Tool = null,
    string? Sql = null,
    QueryResult? Preview = null,
    long? RowCount = null,
    VizSpec? Spec = null,
    string? Error = null,
    long? InputTokens = null,
    long? OutputTokens = null);

/// <summary>
/// Body of <c>POST /api/v1/agent/derived-field</c>: a chart spec that references a field which does not exist yet
/// (<see cref="FieldName"/>) plus a plain-English description of how to compute it.
/// </summary>
public sealed record DerivedFieldRequest(VizSpec Spec, string FieldName, string Hint, Guid? ThreadId = null);

/// <summary>
/// Result of the derived-field flow: the SQL the agent wrote, its explanation, a preview of the new dataset version, the
/// final chart spec (now targeting the new version), the chart rows, and where the step was recorded in the Data Thread.
/// </summary>
public sealed record DerivedFieldResponse(
    string Sql,
    string Explanation,
    QueryResult Preview,
    Guid DatasetVersionId,
    VizSpec Spec,
    VizQueryResponse Chart,
    Guid ThreadId,
    Guid ThreadNodeId);
