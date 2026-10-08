using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InsightFlow.Agents.Sandbox;
using InsightFlow.Contracts.Agents;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Validation;
using InsightFlow.Domain.Viz;
using Microsoft.Extensions.AI;

namespace InsightFlow.Agents.Analyst;

/// <summary>
/// The analyst's tools — all read-only or sandboxed (D7). Each tool reports what it did to the event stream, so the user
/// always sees the SQL and the data behind an answer. Tool results returned to the model are compact text/JSON.
/// </summary>
public sealed class AnalystTools(AnalystContext context, ISqlSandbox sandbox, ChannelWriter<AgentEvent> events)
{
    /// <summary>Rows returned to the model per query (the full preview still goes to the user).</summary>
    public const int RowsForModel = 20;

    /// <summary>Distinct sample values per text column in <see cref="DescribeModel"/>.</summary>
    public const int SampleValuesPerColumn = 8;

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>The last chart proposed by the agent (the host returns it as the final chart).</summary>
    public VizSpec? ProposedChart { get; private set; }

    public IReadOnlyList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(DescribeModel, nameof(DescribeModel)),
        AIFunctionFactory.Create(RunSql, nameof(RunSql)),
        AIFunctionFactory.Create(ProposeChart, nameof(ProposeChart)),
    ];

    [Description("Describe the dataset: table `input`, its columns with types, roles, business names and synonyms, calculated measures, and a few sample values per text column plus min/max of numeric and date columns. Call this first.")]
    public async Task<string> DescribeModel(
        [Description("The dataset version id from the conversation (optional).")] string? datasetVersionId = null,
        CancellationToken cancellationToken = default)
    {
        await events.WriteAsync(new AgentEvent(AgentEventTypes.Tool, Tool: nameof(DescribeModel)), cancellationToken);
        if (datasetVersionId is not null && Guid.TryParse(datasetVersionId, out var requested) && requested != context.Version.Id)
        {
            return "Only the dataset of this conversation can be described.";
        }

        var description = new StringBuilder(context.DescribeSchema());
        var profile = await sandbox.RunAsync(new SandboxRequest(context.Tenant, [context.Version], ProfileSql(), PreviewRows: 500), cancellationToken);
        if (profile.Success && profile.Preview is { } rows)
        {
            description.AppendLine("Sample values:");
            foreach (var group in rows.Rows.GroupBy(r => (string)r[0]!))
            {
                description.Append("- \"").Append(group.Key).Append("\": ").AppendLine(string.Join(", ", group.Select(r => Convert.ToString(r[1], CultureInfo.InvariantCulture))));
            }
        }

        return description.ToString();
    }

    [Description("Run one read-only DuckDB SELECT over the table `input` and get the result schema, row count and first rows. Only SELECT is allowed; no files, no other tables, no Python.")]
    public async Task<string> RunSql(
        [Description("A single DuckDB SELECT statement over `input`.")] string sql,
        CancellationToken cancellationToken = default)
    {
        var result = await sandbox.RunAsync(new SandboxRequest(context.Tenant, [context.Version], sql, PreviewRows: RowsForModel), cancellationToken);
        await events.WriteAsync(new AgentEvent(AgentEventTypes.Sql, Sql: sql, Preview: result.Preview, RowCount: result.RowCount, Error: result.Error), cancellationToken);

        if (!result.Success && result.Status != SandboxStatus.TooManyRows)
        {
            return $"ERROR ({result.Status}): {result.Error}";
        }

        var preview = result.Preview!;
        return JsonSerializer.Serialize(new
        {
            rowCount = result.RowCount,
            truncated = result.Status == SandboxStatus.TooManyRows,
            columns = preview.Columns.Select(c => new { name = c.Name, type = c.Type.ToString() }),
            rows = preview.Rows,
        }, Compact);
    }

    [Description("Propose a chart for the answer. Fields must be columns of `input` or calculated measures. Returns the validated chart or the problems to fix.")]
    public async Task<string> ProposeChart(
        [Description("Bar, Line, Area, Point, Pie, Heatmap or Table.")] string mark,
        [Description("Field on the x axis (category or date). Omit for Pie.")] string? x,
        [Description("Field on the y axis (usually a measure).")] string y,
        [Description("Aggregation for y: Sum, Avg, Count, CountDistinct, Min, Max, Median (omit for calculated measures).")] string? yAggregation = null,
        [Description("Date truncation for x: Year, Quarter, Month, Week, Day or Hour.")] string? xTimeUnit = null,
        [Description("Optional field for colour/series (for Pie: the category).")] string? color = null,
        [Description("Sort by y descending (e.g. for rankings).")] bool sortDescending = false,
        CancellationToken cancellationToken = default)
    {
        await events.WriteAsync(new AgentEvent(AgentEventTypes.Tool, Tool: nameof(ProposeChart)), cancellationToken);
        if (!Enum.TryParse<Mark>(mark, true, out var parsedMark))
        {
            return $"Unknown mark '{mark}'. Use Bar, Line, Area, Point, Pie, Heatmap or Table.";
        }

        var yRef = new FieldRef(y, Enum.TryParse<Agg>(yAggregation, true, out var agg) ? agg : Agg.None);
        var xRef = x is null ? null : new FieldRef(x, TimeUnit: Enum.TryParse<TimeUnit>(xTimeUnit, true, out var unit) ? unit : null);
        var spec = new VizSpec(
            VizSpec.CurrentSchemaVersion,
            context.Version.Id,
            parsedMark,
            new VizEncoding(xRef, yRef, Color: color is null ? null : new FieldRef(color)),
            [],
            sortDescending ? [new SortSpec(y, SortDirection.Desc)] : null);

        var validation = VizSpecValidator.Validate(spec, context.Model);
        if (!validation.IsValid)
        {
            return "The chart is invalid: " + string.Join("; ", validation.Errors.Select(e => $"{e.Path}: {e.Message}"));
        }

        ProposedChart = spec;
        await events.WriteAsync(new AgentEvent(AgentEventTypes.Chart, Spec: spec), cancellationToken);
        return "Chart accepted: " + JsonSerializer.Serialize(spec, VizJsonContext.Default.VizSpec);
    }

    /// <summary>Trusted profiling SQL built from schema identifiers (quoted), never from model or user text.</summary>
    internal string ProfileSql()
    {
        var parts = new List<string>();
        foreach (var column in context.Version.Schema.Columns.Take(40))
        {
            var quoted = "\"" + column.Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            var label = "'" + column.Name.Replace("'", "''", StringComparison.Ordinal) + "'";
            if (column.DataType is DataType.String)
            {
                parts.Add($"SELECT {label} AS column_name, CAST(v AS VARCHAR) AS value FROM (SELECT DISTINCT {quoted} AS v FROM input WHERE {quoted} IS NOT NULL ORDER BY 1 LIMIT {SampleValuesPerColumn})");
            }
            else if (column.DataType.IsNumeric() || column.DataType.IsTemporal())
            {
                parts.Add($"SELECT {label} AS column_name, 'min ' || CAST(MIN({quoted}) AS VARCHAR) || ', max ' || CAST(MAX({quoted}) AS VARCHAR) AS value FROM input");
            }
        }

        return parts.Count == 0 ? "SELECT 'none' AS column_name, '' AS value" : string.Join("\nUNION ALL\n", parts);
    }
}
