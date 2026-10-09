using InsightFlow.Connectors.Documents;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Security;
using InsightFlow.Testing;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace InsightFlow.Connectors.Tests;

/// <summary>
/// MongoDB contract tests. Set <c>INSIGHTFLOW_TEST_MONGODB</c> to a connection string of a database where the tests
/// may create the collection <c>insightflow_contract_orders</c>. Without it the contract tests are skipped.
/// </summary>
public sealed class MongoDbConnectorTests : ConnectorContractTests
{
    public const string EnvironmentVariable = "INSIGHTFLOW_TEST_MONGODB";
    private const string CollectionName = "insightflow_contract_orders";
    private const int Rows = 250;

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private static readonly Lazy<Task> SeedOnce = new(SeedAsync);
    private readonly InMemorySecretStore _secrets = new();

    protected override DataSourceKind ExpectedKind => DataSourceKind.MongoDb;

    protected override string ExpectedTableId => CollectionName;

    protected override long ExpectedRowCount => Rows;

    protected override IReadOnlyCollection<string> ExpectedColumns => ["id", "region", "amount", "address.city"];

    protected override IDataSourceConnector CreateConnector() =>
        new MongoDbConnector(_secrets, Options.Create(new ConnectorOptions()));

    protected override void SkipIfUnavailable() =>
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), $"Set {EnvironmentVariable} to run MongoDB contract tests.");

    protected override async Task<ConnectionProfile> CreateValidProfileAsync()
    {
        await SeedOnce.Value;
        var url = new MongoUrl(ConnectionString);
        SecretReference? secret = null;
        var settings = new Dictionary<string, string>
        {
            ["host"] = url.Server.Host,
            ["port"] = url.Server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["database"] = url.DatabaseName!,
        };
        if (!string.IsNullOrEmpty(url.Username))
        {
            secret = await _secrets.SaveAsync(RetailModel.Tenant, url.Password!, Ct);
            settings["user"] = url.Username;
            if (!string.IsNullOrEmpty(url.AuthenticationSource))
            {
                settings["authSource"] = url.AuthenticationSource;
            }
        }

        return ConnectionProfile.Create(RetailModel.Tenant, "Contract test", DataSourceKind.MongoDb, settings, secret, "test", DateTimeOffset.UtcNow);
    }

    protected override ConnectionProfile CreateUnreachableProfile()
    {
        var secret = _secrets.SaveAsync(RetailModel.Tenant, "wrong-password", Ct).GetAwaiter().GetResult();
        return ConnectionProfile.Create(RetailModel.Tenant, "Unreachable", DataSourceKind.MongoDb, new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = "1",
            ["database"] = "nope",
            ["user"] = "nobody",
        }, secret, "test", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Flatten_NestsObjectsAndStoresArraysAsJson()
    {
        var document = new BsonDocument
        {
            { "id", 7 },
            { "address", new BsonDocument("city", "Paris") },
            { "tags", new BsonArray { "a", "b" } },
        };

        var row = MongoDbConnector.Flatten(document);

        row["id"].Type.ShouldBe(DataType.Integer);
        row["address.city"].Value.ShouldBe("Paris");
        row["tags"].Type.ShouldBe(DataType.Json);
        row["tags"].Value.ShouldBe("[\"a\",\"b\"]");
    }

    [Fact]
    public async Task BuildConnectionString_UsesSecretAndDoesNotStoreItOnTheProfile()
    {
        var secret = await _secrets.SaveAsync(RetailModel.Tenant, "s3cr3t!", Ct);
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.MongoDb, new Dictionary<string, string>
        {
            ["host"] = "db.example.com",
            ["database"] = "sales",
            ["user"] = "reader",
        }, secret, "t", DateTimeOffset.UtcNow);

        var url = await ((MongoDbConnector)CreateConnector()).BuildConnectionStringAsync(profile, Ct);

        var parsed = new MongoUrl(url);
        parsed.Password.ShouldBe("s3cr3t!");
        parsed.Username.ShouldBe("reader");
        profile.Settings.Values.ShouldNotContain("s3cr3t!");
    }

    [Fact]
    public async Task Test_MissingSettings_FailsWithClearMessage()
    {
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.MongoDb, new Dictionary<string, string>(), null, "t", DateTimeOffset.UtcNow);
        var result = await CreateConnector().TestAsync(profile, Ct);
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("host");
    }

    private static async Task SeedAsync()
    {
        var url = new MongoUrl(ConnectionString);
        var client = new MongoClient(url);
        var collection = client.GetDatabase(url.DatabaseName).GetCollection<BsonDocument>(CollectionName);
        await collection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
        var docs = new List<BsonDocument>(Rows);
        string?[] regions = ["North", "South", "East", null];
        for (var i = 1; i <= Rows; i++)
        {
            docs.Add(new BsonDocument
            {
                { "id", i },
                { "region", regions[i % 4] is null ? BsonNull.Value : regions[i % 4] },
                { "amount", i * 1.25 },
                { "address", new BsonDocument("city", "Paris") },
            });
        }

        await collection.InsertManyAsync(docs);
    }
}
