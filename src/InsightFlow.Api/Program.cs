using InsightFlow.Logic.DependencyInjection;
using InsightFlow.Api.Documents;
using InsightFlow.Api.Playground;
using InsightFlow.Api.Skills;
using InsightFlow.Api;
using InsightFlow.Api.Admin;
using InsightFlow.Api.Connections;
using InsightFlow.Api.Knowledge;
using InsightFlow.Api.Workspace;
using InsightFlow.Infrastructure.Connectors;
using InsightFlow.Domain.Contracts;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Repository;
using InsightFlow.Foundation.Security;
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

builder.Services.AddInsightFlowLogic(builder.Configuration);

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
v1.MapGroup("/admin").WithTags("Admin").MapAdminEndpoints();
v1.MapGroup("/navigation").WithTags("Navigation").MapNavigationEndpoints();
v1.MapGroup("/knowledge").WithTags("Knowledge").MapKnowledgeEndpoints();
v1.MapGroup("/skills").WithTags("Skills").MapSkillEndpoints();
v1.MapGroup("/playground").WithTags("Playground").MapPlaygroundEndpoints();
v1.MapGroup("/query").WithTags("Query").MapQueryEndpoints();
v1.MapGroup("/documents").WithTags("Documents").MapDocumentEndpoints();
v1.MapGroup("/connections").WithTags("Connections").MapConnectionEndpoints();
v1.MapGroup("/extract-runs").WithTags("Extracts").MapExtractRunEndpoints();

app.Run();

static string DefaultLocalSecretsPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InsightFlow", "secrets.local.json");
