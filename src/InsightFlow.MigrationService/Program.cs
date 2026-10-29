using InsightFlow.MigrationService;
using InsightFlow.Repository;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddInsightFlowPersistenceForSystem(builder.Configuration.GetConnectionString("insightflow"));
builder.EnrichNpgsqlDbContext<InsightFlowDbContext>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DevelopmentDataSeeder>();
builder.Services.AddHostedService<MigrationWorker>();

var host = builder.Build();
host.Run();
