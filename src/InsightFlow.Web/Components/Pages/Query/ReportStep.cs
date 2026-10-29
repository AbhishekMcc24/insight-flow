using InsightFlow.Domain.Contracts.Agents;

namespace InsightFlow.Web.Components.Pages.Query;

/// <summary>One step of how an answer was produced, as the report shows it. <see cref="Usage"/> is null for steps that use no AI.</summary>
public sealed record ReportStep(string Key, string Title, string Detail, double DurationMs, UsageDto? Usage = null);
