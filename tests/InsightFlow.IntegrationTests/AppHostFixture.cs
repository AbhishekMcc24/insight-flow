using InsightFlow.Persistence;
using Microsoft.EntityFrameworkCore;

[assembly: AssemblyFixture(typeof(InsightFlow.IntegrationTests.AppHostFixture))]

namespace InsightFlow.IntegrationTests;

/// <summary>
/// Starts the whole Aspire AppHost once per test run (containers, migration service, all services) and shares it
/// across test classes. When Docker is not running the app is not started and every test using it is skipped.
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(6);
    private static readonly string[] HttpServices = ["queryservice", "agentservice", "api", "web"];

    private DistributedApplication? _app;
    private string? _connectionString;

    public DistributedApplication App => _app ?? throw new InvalidOperationException("The AppHost is not running.");

    /// <summary>Connection string of the migrated <c>insightflow</c> database.</summary>
    public string ConnectionString => _connectionString ?? throw new InvalidOperationException("The AppHost is not running.");

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(StartupTimeout);
        var ct = timeout.Token;

        // Fresh containers per run: tests never touch the developer's persistent local data.
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.InsightFlow_AppHost>(
            ["--InsightFlow:EphemeralInfrastructure=true"], ct);
        _app = await builder.BuildAsync(ct);
        await _app.StartAsync(ct);

        await _app.ResourceNotifications.WaitForResourceAsync("migrations", KnownResourceStates.Finished, ct);
        foreach (var service in HttpServices)
        {
            await _app.ResourceNotifications.WaitForResourceHealthyAsync(service, ct);
        }

        _connectionString = await _app.GetConnectionStringAsync("insightflow", ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    /// <summary>Skips the calling test when the app could not be started (no Docker).</summary>
    public void RequireRunning() =>
        Assert.SkipWhen(_app is null, "The AppHost is not running (Docker unavailable); start Docker Desktop to run integration tests.");

    public HttpClient CreateHttpClient(string resourceName) => App.CreateHttpClient(resourceName);

    /// <summary>A DbContext against the real database, scoped to <paramref name="tenant"/>.</summary>
    public InsightFlowDbContext CreateDbContext(ICurrentTenant tenant) =>
        new(new DbContextOptionsBuilder<InsightFlowDbContext>().UseNpgsql(ConnectionString).Options, tenant);
}
