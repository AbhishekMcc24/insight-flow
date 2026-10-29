using System.Net;

namespace InsightFlow.Tests.Infrastructure.Connectors;

public sealed class KnowledgeEndpointPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void IsInternal_PrivateAndLocalAddresses_AreBlocked(string address) =>
        KnowledgeEndpointPolicy.IsInternal(IPAddress.Parse(address)).ShouldBeTrue();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void IsInternal_PublicAddresses_AreAllowed(string address) =>
        KnowledgeEndpointPolicy.IsInternal(IPAddress.Parse(address)).ShouldBeFalse();

    [Fact]
    public async Task EnsureAllowedAsync_LoopbackInProduction_Throws() =>
        await Should.ThrowAsync<KnowledgeGraphException>(() => KnowledgeEndpointPolicy.EnsureAllowedAsync("bolt://127.0.0.1:7687", false, Ct));

    [Fact]
    public async Task EnsureAllowedAsync_LoopbackInDevelopment_IsAllowed() =>
        await KnowledgeEndpointPolicy.EnsureAllowedAsync("bolt://localhost:7687", true, Ct);

    [Fact]
    public async Task EnsureAllowedAsync_PublicLiteral_IsAllowed() =>
        await KnowledgeEndpointPolicy.EnsureAllowedAsync("neo4j+s://8.8.8.8", false, Ct);
}
