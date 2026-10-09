using InsightFlow.Connectors.Documents;
using InsightFlow.Domain.Connections;
using InsightFlow.Testing;
using DataType = InsightFlow.Domain.Modeling.DataType;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace InsightFlow.Connectors.Tests;

/// <summary>
/// Cosmos DB contract tests. Set <c>INSIGHTFLOW_TEST_COSMOS</c> to a connection string
/// (<c>AccountEndpoint=...;AccountKey=...</c>) of an account where the tests may create the database
/// <c>insightflow</c> and container <c>insightflow_contract_orders</c>. Without it the contract tests are skipped.
/// </summary>
public sealed class CosmosDbConnectorTests : ConnectorContractTests
{
    public const string EnvironmentVariable = "INSIGHTFLOW_TEST_COSMOS";
    private const string DatabaseName = "insightflow";
    private const string ContainerName = "insightflow_contract_orders";
    private const int Rows = 250;

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private static readonly Lazy<Task> SeedOnce = new(SeedAsync);
    private readonly InMemorySecretStore _secrets = new();

    protected override DataSourceKind ExpectedKind => DataSourceKind.CosmosDb;

    protected override string ExpectedTableId => ContainerName;

    protected override long ExpectedRowCount => Rows;

    protected override IReadOnlyCollection<string> ExpectedColumns => ["id", "region", "amount", "address.city"];

    protected override IDataSourceConnector CreateConnector() =>
        new CosmosDbConnector(_secrets, Options.Create(new ConnectorOptions()));

    protected override void SkipIfUnavailable() =>
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), $"Set {EnvironmentVariable} to run Cosmos DB contract tests.");

    protected override async Task<ConnectionProfile> CreateValidProfileAsync()
    {
        await SeedOnce.Value;
        var (endpoint, key) = Parse(ConnectionString!);
        var secret = await _secrets.SaveAsync(RetailModel.Tenant, key, Ct);
        return ConnectionProfile.Create(RetailModel.Tenant, "Contract test", DataSourceKind.CosmosDb, new Dictionary<string, string>
        {
            ["endpoint"] = endpoint,
            ["database"] = DatabaseName,
        }, secret, "test", DateTimeOffset.UtcNow);
    }

    protected override ConnectionProfile CreateUnreachableProfile()
    {
        var secret = _secrets.SaveAsync(RetailModel.Tenant, "not-a-real-key", Ct).GetAwaiter().GetResult();
        return ConnectionProfile.Create(RetailModel.Tenant, "Unreachable", DataSourceKind.CosmosDb, new Dictionary<string, string>
        {
            ["endpoint"] = "https://127.0.0.1:1/",
            ["database"] = "nope",
        }, secret, "test", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Flatten_SkipsSystemPropertiesAndFlattensObjects()
    {
        var document = JObject.Parse("""
            {
              "id": "1",
              "address": { "city": "Paris" },
              "tags": ["a", "b"],
              "_etag": "abc",
              "_rid": "rid"
            }
            """);

        var row = CosmosDbConnector.Flatten(document);

        row.ContainsKey("_etag").ShouldBeFalse();
        row.ContainsKey("_rid").ShouldBeFalse();
        row["id"].Value.ShouldBe("1");
        row["address.city"].Value.ShouldBe("Paris");
        row["tags"].Type.ShouldBe(DataType.Json);
    }

    [Fact]
    public async Task Test_MissingEndpoint_FailsWithClearMessage()
    {
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.CosmosDb, new Dictionary<string, string>
        {
            ["database"] = "sales",
        }, null, "t", DateTimeOffset.UtcNow);

        var result = await CreateConnector().TestAsync(profile, Ct);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("endpoint");
    }

    private static async Task SeedAsync()
    {
        var (endpoint, key) = Parse(ConnectionString!);
        using var client = new CosmosClient(endpoint, key);
        var database = await client.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var container = await database.Database.CreateContainerIfNotExistsAsync(ContainerName, "/id");
        for (var i = 1; i <= Rows; i++)
        {
            var doc = new JObject
            {
                ["id"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["region"] = (i % 4) switch { 1 => "North", 2 => "South", 3 => "East", _ => null },
                ["amount"] = i * 1.25,
                ["address"] = new JObject { ["city"] = "Paris" },
            };
            await container.Container.UpsertItemAsync(doc, new PartitionKey(doc["id"]!.ToString()));
        }
    }

    private static (string Endpoint, string Key) Parse(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        return (parts["AccountEndpoint"], parts["AccountKey"]);
    }
}
