using InsightFlow.Connectors.Databases;
using InsightFlow.Domain.Connections;
using InsightFlow.Testing;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace InsightFlow.Connectors.Tests;

/// <summary>
/// Oracle contract tests. Set <c>INSIGHTFLOW_TEST_ORACLE</c> to a connection string of a schema where the tests may
/// create <c>INSIGHTFLOW_CONTRACT_ORDERS</c>. Without it the contract tests are skipped.
/// </summary>
public sealed class OracleConnectorTests : ConnectorContractTests
{
    public const string EnvironmentVariable = "INSIGHTFLOW_TEST_ORACLE";
    private const string TableName = "INSIGHTFLOW_CONTRACT_ORDERS";
    private const int Rows = 250;

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private static readonly Lazy<Task> SeedOnce = new(SeedAsync);
    private readonly InMemorySecretStore _secrets = new();

    protected override DataSourceKind ExpectedKind => DataSourceKind.Oracle;

    protected override string ExpectedTableId
    {
        get
        {
            var user = new OracleConnectionStringBuilder(ConnectionString!).UserID;
            return $"{user.ToUpperInvariant()}.{TableName}";
        }
    }

    protected override long ExpectedRowCount => Rows;

    protected override IReadOnlyCollection<string> ExpectedColumns => ["ID", "REGION", "AMOUNT", "ORDERED_ON"];

    protected override IDataSourceConnector CreateConnector() =>
        new OracleConnector(_secrets, Options.Create(new ConnectorOptions()));

    protected override void SkipIfUnavailable() =>
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), $"Set {EnvironmentVariable} to run Oracle contract tests.");

    protected override async Task<ConnectionProfile> CreateValidProfileAsync()
    {
        await SeedOnce.Value;
        var csb = new OracleConnectionStringBuilder(ConnectionString);
        var secret = await _secrets.SaveAsync(RetailModel.Tenant, csb.Password, Ct);
        return ConnectionProfile.Create(RetailModel.Tenant, "Contract test", DataSourceKind.Oracle, new Dictionary<string, string>
        {
            ["dataSource"] = csb.DataSource,
            ["user"] = csb.UserID,
        }, secret, "test", DateTimeOffset.UtcNow);
    }

    protected override ConnectionProfile CreateUnreachableProfile()
    {
        var secret = _secrets.SaveAsync(RetailModel.Tenant, "wrong-password", Ct).GetAwaiter().GetResult();
        return ConnectionProfile.Create(RetailModel.Tenant, "Unreachable", DataSourceKind.Oracle, new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = "1",
            ["service"] = "nope",
            ["user"] = "nobody",
        }, secret, "test", DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task BuildConnectionString_UsesSecretAndEzConnect()
    {
        var secret = await _secrets.SaveAsync(RetailModel.Tenant, "s3cr3t!", Ct);
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.Oracle, new Dictionary<string, string>
        {
            ["host"] = "db.example.com",
            ["service"] = "sales",
            ["user"] = "reader",
        }, secret, "t", DateTimeOffset.UtcNow);

        var csb = new OracleConnectionStringBuilder(await ((OracleConnector)CreateConnector()).BuildConnectionStringAsync(profile, Ct));

        csb.Password.ShouldBe("s3cr3t!");
        csb.DataSource.ShouldBe("//db.example.com:1521/sales");
        profile.Settings.Values.ShouldNotContain("s3cr3t!");
    }

    [Fact]
    public async Task Test_MissingSettings_FailsWithClearMessage()
    {
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.Oracle, new Dictionary<string, string>(), null, "t", DateTimeOffset.UtcNow);
        var result = await CreateConnector().TestAsync(profile, Ct);
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("user");
    }

    [Theory]
    [InlineData("Orders", "\"Orders\"")]
    [InlineData("we\"ird", "\"we\"\"ird\"")]
    public void QuoteIdentifier_EscapesQuotes(string name, string expected) =>
        OracleConnector.QuoteIdentifier(name).ShouldBe(expected);

    private static async Task SeedAsync()
    {
        await using var connection = new OracleConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            BEGIN
              EXECUTE IMMEDIATE 'DROP TABLE {TableName}';
            EXCEPTION WHEN OTHERS THEN NULL;
            END;
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"CREATE TABLE {TableName} (id NUMBER(10) PRIMARY KEY, region VARCHAR2(20) NULL, amount NUMBER(18,2) NULL, ordered_on TIMESTAMP NULL)";
        await command.ExecuteNonQueryAsync();
        command.CommandText =
            $"""
            INSERT INTO {TableName} (id, region, amount, ordered_on)
            SELECT LEVEL,
                   DECODE(MOD(LEVEL, 4), 1, 'North', 2, 'South', 3, 'East', NULL),
                   LEVEL * 1.25,
                   TIMESTAMP '2025-01-01 00:00:00' + NUMTODSINTERVAL(LEVEL, 'DAY')
            FROM DUAL CONNECT BY LEVEL <= {Rows}
            """;
        await command.ExecuteNonQueryAsync();
    }
}
