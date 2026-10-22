using InsightFlow.Api;
using InsightFlow.Api.Connections;
using InsightFlow.Api.Workspace;
using InsightFlow.Connectors;
using InsightFlow.Contracts;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Workspace;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddInsightFlowSecurity();

builder.Services.AddInsightFlowPersistence(
    builder.Configuration.GetConnectionString("insightflow"),
    sp => sp.GetRequiredService<ITenantContext>().TenantId is { } tenant ? new TenantId(tenant) : null);
builder.EnrichNpgsqlDbContext<InsightFlowDbContext>();

builder.AddRedisDistributedCache("redis");

// Secrets: Key Vault when the AppHost wired one (publish mode), otherwise a per-user local file outside the repo.
var useKeyVault = !string.IsNullOrEmpty(builder.Configuration.GetConnectionString("keyvault"));
if (useKeyVault)
{
    builder.AddAzureKeyVaultClient("keyvault");
}

builder.Services.AddInsightFlowSecretStore(useKeyVault, builder.Configuration["SecretStore:LocalFilePath"] ?? DefaultLocalSecretsPath());
builder.Services.AddInsightFlowConnectors(builder.Configuration);

builder.Services.AddOptions<UploadOptions>()
    .Bind(builder.Configuration.GetSection(UploadOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart()
    .PostConfigure(o => o.StorageRoot = LocalStorage.ChooseRoot(
        builder.Configuration[LocalStorage.RootConfigurationKey],
        builder.Configuration.GetConnectionString(LocalStorage.ConnectionStringName)));
builder.Services.AddSingleton<IFileStore, DirectoryFileStore>();
builder.Services.AddSingleton<IUploadScanner, NoOpUploadScanner>();
builder.Services.AddSingleton<IContentPermissionEvaluator, ContentPermissionEvaluator>();
builder.Services.AddScoped<WorkspaceService>();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default));
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseInsightFlowSecurity();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.MapDefaultEndpoints();

var v1 = app.MapGroup("/api/v1");
v1.MapIdentityEndpoints();
v1.MapGroup("/workspace").WithTags("Workspace").MapWorkspaceEndpoints();
v1.MapGroup("/connections").WithTags("Connections").MapConnectionEndpoints();
v1.MapGroup("/extract-runs").WithTags("Extracts").MapExtractRunEndpoints();

app.Run();

static string DefaultLocalSecretsPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InsightFlow", "secrets.local.json");
