using InsightFlow.Domain.Tenancy;
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
builder.AddAzureBlobServiceClient("blobs");

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

// TODO(roy): M4 — POST /api/v1/query/viz and POST /api/v1/query/preview.
app.MapGroup("/api/v1/query").WithTags("Query");

app.Run();
