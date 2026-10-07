using System.Diagnostics;
using InsightFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.MigrationService;

/// <summary>
/// Applies database migrations and seed data exactly once per deployment, then stops the host.
/// Exists so that schema changes are never raced by multiple service replicas starting at the same time;
/// every other service waits for this resource to complete (<c>WaitForCompletion</c> in the AppHost).
/// A failure sets a non-zero exit code so dependants do not start against a half-migrated database.
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
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied")]
    private static partial void LogMigrationsApplied(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);
}
