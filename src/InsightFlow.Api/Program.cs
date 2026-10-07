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

// TODO(roy): M5 — workspace explorer endpoints (folders, upload, move, create dataset) and connections.
app.MapGroup("/api/v1/workspace").WithTags("Workspace");

app.Run();
