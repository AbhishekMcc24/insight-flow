using InsightFlow.Connectors.Databases;
using InsightFlow.Domain.Connections;
using InsightFlow.Testing;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace InsightFlow.Connectors.Tests;

/// <summary>
/// MySQL contract tests. Set <c>INSIGHTFLOW_TEST_MYSQL</c> to a connection string of a database where the tests may
/// create <c>insightflow_contract_orders</c>. Without it the contract tests are skipped.
/// </summary>
public sealed class MySqlDbConnectorTests : ConnectorContractTests
{
    public const string EnvironmentVariable = "INSIGHTFLOW_TEST_MYSQL";
    private const string TableName = "insightflow_contract_orders";
    private const int Rows = 250;

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private static readonly Lazy<Task> SeedOnce = new(SeedAsync);
    private readonly InMemorySecretStore _secrets = new();

    protected override DataSourceKind ExpectedKind => DataSourceKind.MySql;

    protected override string ExpectedTableId =>
        $"{new MySqlConnectionStringBuilder(ConnectionString!).Database}.{TableName}";

    protected override long ExpectedRowCount => Rows;

    protected override IReadOnlyCollection<string> ExpectedColumns => ["id", "region", "amount", "ordered_on"];

    protected override IDataSourceConnector CreateConnector() =>
        new MySqlDbConnector(_secrets, Options.Create(new ConnectorOptions()));

    protected override void SkipIfUnavailable() =>
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), $"Set {EnvironmentVariable} to run MySQL contract tests.");

    protected override async Task<ConnectionProfile> CreateValidProfileAsync()
    {
        await SeedOnce.Value;
        var csb = new MySqlConnectionStringBuilder(ConnectionString!);
        var secret = await _secrets.SaveAsync(RetailModel.Tenant, csb.Password, Ct);
        var settings = new Dictionary<string, string>
        {
            ["host"] = csb.Server,
            ["port"] = csb.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["database"] = csb.Database,
            ["user"] = csb.UserID,
            ["sslmode"] = "disable",
        };
        return ConnectionProfile.Create(RetailModel.Tenant, "Contract test", DataSourceKind.MySql, settings, secret, "test", DateTimeOffset.UtcNow);
    }

    protected override ConnectionProfile CreateUnreachableProfile()
    {
        var secret = _secrets.SaveAsync(RetailModel.Tenant, "wrong-password", Ct).GetAwaiter().GetResult();
        return ConnectionProfile.Create(RetailModel.Tenant, "Unreachable", DataSourceKind.MySql, new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = "1",
            ["database"] = "nope",
            ["user"] = "nobody",
            ["sslmode"] = "disable",
        }, secret, "test", DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task BuildConnectionString_UsesSecretAndRequiresSsl()
    {
        var secret = await _secrets.SaveAsync(RetailModel.Tenant, "s3cr3t!", Ct);
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.MySql, new Dictionary<string, string>
        {
            ["host"] = "db.example.com",
            ["database"] = "sales",
            ["user"] = "reader",
        }, secret, "t", DateTimeOffset.UtcNow);

        var csb = new MySqlConnectionStringBuilder(await ((MySqlDbConnector)CreateConnector()).BuildConnectionStringAsync(profile, Ct));

        csb.Password.ShouldBe("s3cr3t!");
        csb.SslMode.ShouldBe(MySqlSslMode.Required);
        profile.Settings.Values.ShouldNotContain("s3cr3t!");
    }

    [Fact]
    public async Task Test_MissingSettings_FailsWithClearMessage()
    {
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.MySql, new Dictionary<string, string>(), null, "t", DateTimeOffset.UtcNow);
        var result = await CreateConnector().TestAsync(profile, Ct);
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("host");
    }

    [Theory]
    [InlineData("Orders", "`Orders`")]
    [InlineData("we`ird", "`we``ird`")]
    public void QuoteIdentifier_EscapesBackticks(string name, string expected) =>
        MySqlDbConnector.QuoteIdentifier(name).ShouldBe(expected);

    private static async Task SeedAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            DROP TABLE IF EXISTS {TableName};
            CREATE TABLE {TableName} (id INT NOT NULL PRIMARY KEY, region VARCHAR(20) NULL, amount DECIMAL(18,2) NULL, ordered_on DATETIME NULL);
            INSERT INTO {TableName} (id, region, amount, ordered_on)
            WITH RECURSIVE n AS (SELECT 1 AS i UNION ALL SELECT i + 1 FROM n WHERE i < {Rows})
            SELECT i, ELT(1 + (i % 4), 'North', 'South', 'East', NULL), i * 1.25, DATE_ADD('2025-01-01', INTERVAL i DAY) FROM n;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
