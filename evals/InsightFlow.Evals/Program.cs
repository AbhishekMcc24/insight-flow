using InsightFlow.Agents.Ai;
using InsightFlow.Agents.Analyst;
using InsightFlow.Agents.Sandbox;
using InsightFlow.Domain.Threads;
using InsightFlow.Evals;
using InsightFlow.Query.Storage;
using InsightFlow.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// Usage:
//   dotnet run --project evals/InsightFlow.Evals -- --provider anthropic|azure-openai|reference [--limit N] [--questions path] [--out dir]
// Keys: ANTHROPIC_API_KEY, or AZURE_OPENAI_ENDPOINT (+ AZURE_OPENAI_API_KEY, or AZURE_OPENAI_USE_ENTRA_ID=true) and optionally
// AZURE_OPENAI_DEPLOYMENT_LARGE / _SMALL. "reference" replays the reference SQL (no model) to validate the harness itself.
var options = ParseArgs(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var startedAt = DateTimeOffset.UtcNow;
var questions = new DeserializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build()
    .Deserialize<EvalQuestionSet>(await File.ReadAllTextAsync(options.QuestionsPath, cts.Token))
    .Questions
    .Take(options.Limit)
    .ToList();

// Deterministic dataset: the same generator and row count as the Contoso sample data.
const int DatasetRows = 5_000;
var work = Path.Combine(Path.GetTempPath(), "insightflow-evals", Guid.NewGuid().ToString("N"));
var parquet = Path.Combine(work, "retail_sales.parquet");
await RetailDataGenerator.WriteDenormalizedAsync(parquet, DatasetRows, csv: false, cts.Token);
var tenant = RetailModel.Tenant;
var version = DatasetVersion.CreateSource(Guid.NewGuid(), tenant, RetailModel.DenormalizedSchema, DatasetRows, "evals", startedAt);
var sandbox = new DuckDbSqlSandbox(
    new SingleFileExtractStore(work, version.Id, parquet),
    Options.Create(new SandboxOptions { WorkDirectory = Path.Combine(work, "sandbox"), DefaultTimeout = TimeSpan.FromSeconds(30) }),
    NullLogger<DuckDbSqlSandbox>.Instance);
var runner = new EvalRunner(sandbox, tenant, version);

EvalRunner.Answerer answerer;
string model;
if (options.Provider == "reference")
{
    answerer = EvalRunner.ReferenceAnswerer();
    model = "reference-sql";
    if (!await SelfCheckAsync(runner, questions, cts.Token))
    {
        return 3;
    }
}
else
{
    var services = BuildAiServices(options.Provider);
    var router = services.GetRequiredService<IModelRouter>();
    if (router.AvailableRoutes.Count == 0)
    {
        Console.Error.WriteLine($"No credentials for provider '{options.Provider}'. See the usage comment at the top of Program.cs.");
        return 2;
    }

    model = router.Resolve(tenant, AiTask.SqlGeneration).ModelId;
    var analyst = new AnalystAgent(router, sandbox, services.GetRequiredService<ILogger<AnalystAgent>>());
    answerer = EvalRunner.AgentAnswerer(analyst, new AnalystContext(tenant, version, RetailModel.CreateSample(tenant, version.Id)));
}

Console.WriteLine($"Running {questions.Count} question(s) with provider '{options.Provider}' ({model})...");
var results = new List<EvalResult>();
foreach (var question in questions)
{
    var result = await runner.RunAsync(question, answerer, cts.Token);
    results.Add(result);
    Console.WriteLine($"  {result.Id} {(result.Passed ? "PASS" : "FAIL")}  {result.Reason}");
}

var report = new EvalReport(options.Provider, model, startedAt, DatasetRows, results);
var (markdownPath, jsonPath) = await report.WriteAsync(options.OutputDirectory, cts.Token);
Console.WriteLine($"Accuracy {report.Passed}/{results.Count} ({report.Accuracy:P0}). Report: {markdownPath} and {jsonPath}");

try
{
    Directory.Delete(work, recursive: true);
}
catch (IOException)
{
}

return options.Provider == "reference" && report.Passed != results.Count ? 1 : 0;

static (string Provider, int Limit, string QuestionsPath, string OutputDirectory) ParseArgs(string[] args)
{
    string Get(string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    var provider = Get("--provider", "reference").ToLowerInvariant();
    if (provider is not ("reference" or "anthropic" or "azure-openai"))
    {
        throw new ArgumentException($"Unknown provider '{provider}'. Use anthropic, azure-openai or reference.");
    }

    return (
        provider,
        int.Parse(Get("--limit", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.CultureInfo.InvariantCulture),
        Get("--questions", Path.Combine(AppContext.BaseDirectory, "questions.yaml")),
        Get("--out", Path.Combine(Directory.GetCurrentDirectory(), "eval-results")));
}

static ServiceProvider BuildAiServices(string provider)
{
    // Route every task to the chosen provider so the report measures exactly one provider.
    var settings = new Dictionary<string, string?>
    {
        ["Ai:Anthropic:ApiKey"] = provider == "anthropic" ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : null,
        ["Ai:AzureOpenAI:Endpoint"] = provider == "azure-openai" ? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") : null,
        ["Ai:AzureOpenAI:ApiKey"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
        ["Ai:AzureOpenAI:UseEntraId"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_USE_ENTRA_ID"),
        ["Ai:DefaultMonthlyTokenBudget"] = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
    foreach (var size in new[] { "large", "small" })
    {
        if (Environment.GetEnvironmentVariable($"AZURE_OPENAI_DEPLOYMENT_{size.ToUpperInvariant()}") is { Length: > 0 } deployment)
        {
            settings[$"Ai:AzureOpenAI:Deployments:{size}"] = deployment;
        }
    }

    foreach (var task in Enum.GetNames<AiTask>())
    {
        settings[$"Ai:Routes:{task}"] = $"{provider}:large";
    }

    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(settings)
        .AddUserSecrets(typeof(EvalRunner).Assembly, optional: true)
        .Build();
    return new ServiceCollection()
        .AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning))
        .AddInsightFlowAi(configuration)
        .BuildServiceProvider();
}

// The comparer must FAIL on a wrong answer, or a 100 % score would mean nothing.
static async Task<bool> SelfCheckAsync(EvalRunner runner, IReadOnlyList<EvalQuestion> questions, CancellationToken ct)
{
    var wrong = questions.FirstOrDefault(q => q.Id == "q03");
    if (wrong is null)
    {
        return true;
    }

    var result = await runner.RunAsync(wrong, (_, _) =>
        Task.FromResult<(string?, long, long, string?)>(("SELECT region, SUM(cost) FROM input GROUP BY region", 0, 0, null)), ct);
    if (result.Passed)
    {
        Console.Error.WriteLine("Self-check failed: a wrong answer was scored as correct.");
        return false;
    }

    return true;
}

/// <summary>Serves the eval dataset's Parquet file to the sandbox.</summary>
internal sealed class SingleFileExtractStore(string root, Guid versionId, string path) : IExtractStore
{
    public string LocalRoot => root;

    public Task<string> GetLocalPathAsync(DatasetVersion version, CancellationToken cancellationToken) =>
        version.Id == versionId ? Task.FromResult(path) : throw new InvalidOperationException("Unknown dataset version.");

    public Task<Uri> SaveAsync(InsightFlow.Domain.Tenancy.TenantId tenant, Guid datasetVersionId, Stream parquet, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
