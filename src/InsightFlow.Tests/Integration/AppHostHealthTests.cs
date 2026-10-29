namespace InsightFlow.Tests.Integration;

/// <summary>The whole distributed app starts and every HTTP service reports healthy (anonymously).</summary>
[Trait("Category", "Integration")]
public sealed class AppHostHealthTests(AppHostFixture fixture)
{
    [Theory]
    [InlineData("queryservice")]
    [InlineData("agentservice")]
    [InlineData("api")]
    [InlineData("web")]
    public async Task Service_AfterStartup_ReportsHealthy(string resourceName)
    {
        fixture.RequireRunning();
        using var client = fixture.CreateHttpClient(resourceName);

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue($"{resourceName} /health returned {(int)response.StatusCode}");
    }
}
