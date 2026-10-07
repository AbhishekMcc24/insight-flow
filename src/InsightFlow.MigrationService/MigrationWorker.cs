using System.Diagnostics;

namespace InsightFlow.MigrationService;

/// <summary>
/// Applies database migrations and seed data exactly once per deployment, then stops the host.
/// Exists so that schema changes are never raced by multiple service replicas starting at the same time.
/// </summary>
internal sealed partial class MigrationWorker(
    IHostApplicationLifetime lifetime,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    internal static readonly ActivitySource ActivitySource = new("InsightFlow.Migrations");

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Migrate database", ActivityKind.Client);

        // TODO(roy): M3 — apply EF Core migrations and seed the Contoso Retail tenant.
        LogMigrationsCompleted(logger);

        lifetime.StopApplication();
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations completed")]
    private static partial void LogMigrationsCompleted(ILogger logger);
}
