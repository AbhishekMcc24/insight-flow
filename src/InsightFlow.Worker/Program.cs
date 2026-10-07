var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureBlobServiceClient("blobs");

// TODO(roy): M5 — Quartz.NET with the PostgreSQL job store and ExtractRefreshJob.

var host = builder.Build();
host.Run();
