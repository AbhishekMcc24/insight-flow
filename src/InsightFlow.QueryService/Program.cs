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

// TODO(roy): M4 — POST /api/v1/query/viz and POST /api/v1/query/preview.
app.MapGroup("/api/v1/query").WithTags("Query");

app.Run();
