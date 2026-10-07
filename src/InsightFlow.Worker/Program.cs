using InsightFlow.Connectors;
using InsightFlow.Persistence;
using InsightFlow.Worker.Development;
using InsightFlow.Worker.Extracts;
using Quartz;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureBlobServiceClient("blobs");

var connectionString = builder.Configuration.GetConnectionString("insightflow");
builder.Services.AddInsightFlowPersistenceForJobs(connectionString);
builder.EnrichNpgsqlDbContext<InsightFlowDbContext>();

// Secrets: Key Vault when the AppHost wired one (publish mode), otherwise the same per-user local file the Api writes.
var useKeyVault = !string.IsNullOrEmpty(builder.Configuration.GetConnectionString("keyvault"));
if (useKeyVault)
{
    builder.AddAzureKeyVaultClient("keyvault");
}

builder.Services.AddInsightFlowSecretStore(
    useKeyVault,
    builder.Configuration["SecretStore:LocalFilePath"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InsightFlow", "secrets.local.json"));
builder.Services.AddInsightFlowConnectors(builder.Configuration);
builder.Services.AddSingleton<ExtractRunProcessor>();

// Quartz.NET with the clustered PostgreSQL job store (D15). Quartz owns its qrtz_* tables and creates them if missing.
builder.Services.AddQuartz("insightflow-worker", q =>
{
    // Unique instance ids are required for clustering across replicas.
    q.ConfigureScheduler(o => o.GenerateInstanceId = true);
    q.UsePersistentStore(store =>
    {
        store.UsePostgres(connectionString!);
        store.UseSystemTextJsonSerializer();
        store.UseClustering();
        store.ProvisionSchema();
    });

    q.AddJob<ExtractRefreshJob>(job => job.WithIdentity(ExtractRefreshJob.Key).StoreDurably());
    q.AddTrigger(trigger => trigger
        .ForJob(ExtractRefreshJob.Key)
        .WithIdentity("extract-refresh-poll", "extracts")
        .StartNow()
        .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(3)).RepeatForever().WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithRemainingCount)));
});
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<DevelopmentSampleDataService>();
}

var host = builder.Build();
host.Run();
