using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisDistributedCache("redis");
builder.AddAzureBlobServiceClient("blobs");

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapDefaultEndpoints();

// TODO(roy): M6 — POST /api/v1/agent/analyst (SSE) and the derived-field endpoint.
app.MapGroup("/api/v1/agent").WithTags("Agents");

app.Run();
