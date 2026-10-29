namespace InsightFlow.Foundation;

/// <summary>
/// Naming convention for custom telemetry. Each library owns its own <c>ActivitySource</c> and <c>Meter</c>
/// named <c>InsightFlow.&lt;Area&gt;</c> (e.g. <c>InsightFlow.Infrastructure.Query</c>, <c>InsightFlow.Agent</c>), so traces show
/// compile → execute → cache spans. Foundation subscribes to all of them via a wildcard, which means
/// libraries never need to reference this project.
/// </summary>
public static class InsightFlowTelemetry
{
    /// <summary>Prefix shared by every Insight Flow activity source and meter.</summary>
    public const string Prefix = "InsightFlow.";

    /// <summary>Wildcard passed to <c>AddSource</c>.</summary>
    public const string ActivitySourceWildcard = Prefix + "*";

    /// <summary>Wildcard passed to <c>AddMeter</c>.</summary>
    public const string MeterWildcard = Prefix + "*";
}
