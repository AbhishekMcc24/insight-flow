namespace InsightFlow.IntegrationTests;

/// <summary>Milestone 1 smoke test: the whole distributed app starts and every HTTP service reports healthy.</summary>
public sealed class AppHostHealthTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData("queryservice")]
    [InlineData("agentservice")]
    [InlineData("api")]
    [InlineData("web")]
    public async Task Service_AfterStartup_ReportsHealthy(string resourceName)
    {
        DockerAvailability.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartupTimeout);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.InsightFlow_AppHost>(timeout.Token);
        await using var app = await appHost.BuildAsync(timeout.Token);
        await app.StartAsync(timeout.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(resourceName, timeout.Token);

        using var client = app.CreateHttpClient(resourceName);
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), timeout.Token);

        response.IsSuccessStatusCode.ShouldBeTrue($"{resourceName} /health returned {(int)response.StatusCode}");
    }
}
