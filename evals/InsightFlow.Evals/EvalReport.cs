using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InsightFlow.Evals;

/// <summary>Accuracy report for one provider run, written as Markdown (for humans/PRs) and JSON (for trend tracking).</summary>
public sealed record EvalReport(
    string Provider,
    string Model,
    DateTimeOffset StartedAt,
    int DatasetRows,
    IReadOnlyList<EvalResult> Results)
{
    public int Passed => Results.Count(r => r.Passed);

    public double Accuracy => Results.Count == 0 ? 0 : (double)Passed / Results.Count;

    public long InputTokens => Results.Sum(r => r.InputTokens);

    public long OutputTokens => Results.Sum(r => r.OutputTokens);

    public async Task<(string Markdown, string Json)> WriteAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var stamp = StartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var markdown = Path.Combine(directory, $"eval-{Provider}-{stamp}.md");
        var json = Path.Combine(directory, $"eval-{Provider}-{stamp}.json");

        await File.WriteAllTextAsync(markdown, ToMarkdown(), cancellationToken);
        await File.WriteAllTextAsync(json, JsonSerializer.Serialize(new
        {
            provider = Provider,
            model = Model,
            startedAt = StartedAt,
            datasetRows = DatasetRows,
            passed = Passed,
            total = Results.Count,
            accuracy = Accuracy,
            inputTokens = InputTokens,
            outputTokens = OutputTokens,
            results = Results,
        }, JsonOptions), cancellationToken);
        return (markdown, json);
    }

    public string ToMarkdown()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"# Insight Flow analyst eval — {Provider}");
        sb.AppendLine();
        sb.AppendLine(inv, $"- Model: `{Model}`");
        sb.AppendLine(inv, $"- Run: {StartedAt:yyyy-MM-dd HH:mm} UTC, dataset {DatasetRows:N0} rows");
        sb.AppendLine(inv, $"- **Accuracy: {Passed}/{Results.Count} ({Accuracy:P0})**");
        sb.AppendLine(inv, $"- Tokens: {InputTokens:N0} in / {OutputTokens:N0} out");
        sb.AppendLine();
        sb.AppendLine("| # | Question | Result | Reason | Time (s) |");
        sb.AppendLine("|---|---|---|---|---:|");
        foreach (var r in Results)
        {
            sb.AppendLine(inv, $"| {r.Id} | {Escape(r.Question)} | {(r.Passed ? "✅" : "❌")} | {Escape(r.Reason)} | {r.Seconds:F1} |");
        }

        return sb.ToString();
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
