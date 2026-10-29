using Microsoft.Extensions.Logging.Abstractions;

namespace InsightFlow.Tests.Infrastructure.Connectors;

public sealed class KnowledgeGraphGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("neo4j+s://abc.databases.neo4j.io", "neo4j://abc.databases.neo4j.io")]
    [InlineData("neo4j+ssc://abc.databases.neo4j.io", "neo4j://abc.databases.neo4j.io")]
    [InlineData("bolt+s://host:7687", "bolt://host:7687")]
    [InlineData("bolt://host:7687", "bolt://host:7687")]
    [InlineData("neo4j://abc.databases.neo4j.io", "neo4j://abc.databases.neo4j.io")]
    public void EncryptionByConfiguration_SchemeSaysEncrypted_BecomesThePlainScheme(string uri, string expected) =>
        Neo4jKnowledgeGraphGateway.EncryptionByConfiguration(uri).ShouldBe(expected);

    [Fact]
    public async Task TestAsync_AuraSchemeWithEncryption_DoesNotTripTheDriversSchemeConflict()
    {
        var gateway = new Neo4jKnowledgeGraphGateway(NullLogger<Neo4jKnowledgeGraphGateway>.Instance);

        var result = await gateway.TestAsync(
            new KnowledgeConnectionSettings("neo4j+s://does-not-exist.invalid", "neo4j", "x", "neo4j", UseAura: true, Encrypted: true), Ct);

        result.Succeeded.ShouldBeFalse(); // no such server, but the failure must be about reaching it
        result.Message.ShouldNotContain("cannot both be set");
    }
}
