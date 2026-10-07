using InsightFlow.Contracts;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Persistence;
using InsightFlow.Query;
using InsightFlow.QueryService;
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
builder.AddAzureBlobServiceClient("blobs");
builder.Services.AddInsightFlowQueryEngine(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default));
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

app.MapGroup("/api/v1/query")
    .WithTags("Query")
    .MapQueryEndpoints();

app.Run();
