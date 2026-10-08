using System.Diagnostics;
using InsightFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.MigrationService;

/// <summary>
/// Applies database migrations and seed data exactly once per deployment, then stops the host.
/// Exists so that schema changes are never raced by multiple service replicas starting at the same time;
/// every other service waits for this resource to complete (<c>WaitForCompletion</c> in the AppHost).
/// A failure sets a non-zero exit code so dependants do not start against a half-migrated database.
/// With <c>Migrations:KeepAlive=true</c> (set by the AppHost when publishing to Azure Container Apps, which has no
/// run-to-completion apps in Aspire 13.6) the host stays up idle after a successful run instead of exiting, so the
/// platform does not restart it in a loop. TODO(dev2): switch to an Azure Container Apps Job once Aspire supports it.
/// </summary>
internal sealed partial class MigrationWorker(
    IServiceProvider services,
    IHostEnvironment environment,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    internal static readonly ActivitySource ActivitySource = new("InsightFlow.Migrations");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Migrate database", ActivityKind.Client);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InsightFlowDbContext>();

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(() => db.Database.MigrateAsync(stoppingToken));
            LogMigrationsApplied(logger);

            if (environment.IsDevelopment() || configuration.GetValue<bool>("Seed:DevelopmentData"))
            {
                var seeder = scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>();
                await strategy.ExecuteAsync(() => seeder.SeedAsync(stoppingToken));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.AddException(ex);
            LogMigrationFailed(logger, ex);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
            return;
        }

        activity?.Dispose();
        if (configuration.GetValue<bool>("Migrations:KeepAlive"))
        {
            LogKeepingAlive(logger);
            await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(static _ => { }, TaskScheduler.Default);
            return;
        }

        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied")]
    private static partial void LogMigrationsApplied(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrations done; staying alive (Migrations:KeepAlive)")]
    private static partial void LogKeepingAlive(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);
}
