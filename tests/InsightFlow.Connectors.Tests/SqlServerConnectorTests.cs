using InsightFlow.Connectors.Databases;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Modeling;
using InsightFlow.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace InsightFlow.Connectors.Tests;

/// <summary>
/// SQL Server contract tests. They need a real server: set <c>INSIGHTFLOW_TEST_SQLSERVER</c> to a SQL-authentication
/// connection string of a database where the tests may create <c>dbo.insightflow_contract_orders</c>. Without it the
/// contract tests are skipped; the offline unit tests below always run.
/// </summary>
public sealed class SqlServerConnectorTests : ConnectorContractTests
{
    public const string EnvironmentVariable = "INSIGHTFLOW_TEST_SQLSERVER";
    private const string TableName = "insightflow_contract_orders";
    private const int Rows = 250;

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private static readonly Lazy<Task> SeedOnce = new(SeedAsync);

    private readonly InMemorySecretStore _secrets = new();

    protected override DataSourceKind ExpectedKind => DataSourceKind.SqlServer;

    protected override string ExpectedTableId => $"dbo.{TableName}";

    protected override long ExpectedRowCount => Rows;

    protected override IReadOnlyCollection<string> ExpectedColumns => ["id", "region", "amount", "ordered_on"];

    protected override IDataSourceConnector CreateConnector() =>
        new SqlServerConnector(_secrets, Options.Create(new ConnectorOptions()));

    protected override void SkipIfUnavailable() =>
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), $"Set {EnvironmentVariable} to run SQL Server contract tests.");

    protected override async Task<ConnectionProfile> CreateValidProfileAsync()
    {
        await SeedOnce.Value;
        var csb = new SqlConnectionStringBuilder(ConnectionString);
        var secret = await _secrets.SaveAsync(RetailModel.Tenant, csb.Password, Ct);
        return ConnectionProfile.Create(RetailModel.Tenant, "Contract test", DataSourceKind.SqlServer, new Dictionary<string, string>
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["user"] = csb.UserID,
            ["trustServerCertificate"] = csb.TrustServerCertificate ? "true" : "false",
        }, secret, "test", DateTimeOffset.UtcNow);
    }

    protected override ConnectionProfile CreateUnreachableProfile()
    {
        var secret = _secrets.SaveAsync(RetailModel.Tenant, "wrong", Ct).GetAwaiter().GetResult();
        return ConnectionProfile.Create(RetailModel.Tenant, "Unreachable", DataSourceKind.SqlServer, new Dictionary<string, string>
        {
            ["server"] = "tcp:127.0.0.1,1",
            ["database"] = "nope",
            ["user"] = "nobody",
        }, secret, "test", DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task BuildConnectionString_UsesSecretAndSecureDefaults()
    {
        var secret = await _secrets.SaveAsync(RetailModel.Tenant, "s3cr3t!", Ct);
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.SqlServer, new Dictionary<string, string>
        {
            ["server"] = "sql.example.com",
            ["database"] = "sales",
            ["user"] = "reader",
        }, secret, "t", DateTimeOffset.UtcNow);

        var csb = new SqlConnectionStringBuilder(await ((SqlServerConnector)CreateConnector()).BuildConnectionStringAsync(profile, Ct));

        csb.Password.ShouldBe("s3cr3t!");
        csb.Encrypt.ShouldBe(SqlConnectionEncryptOption.Mandatory);
        csb.TrustServerCertificate.ShouldBeFalse();
        csb.ApplicationIntent.ShouldBe(ApplicationIntent.ReadOnly);
        profile.Settings.Values.ShouldNotContain("s3cr3t!");
    }

    [Fact]
    public async Task BuildConnectionString_ActiveDirectoryDefault_NeedsNoSecret()
    {
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.SqlServer, new Dictionary<string, string>
        {
            ["server"] = "myserver.database.windows.net",
            ["database"] = "sales",
            ["authentication"] = "ActiveDirectoryDefault",
        }, null, "t", DateTimeOffset.UtcNow);

        var csb = new SqlConnectionStringBuilder(await ((SqlServerConnector)CreateConnector()).BuildConnectionStringAsync(profile, Ct));

        csb.Authentication.ShouldBe(SqlAuthenticationMethod.ActiveDirectoryDefault);
        csb.Password.ShouldBeEmpty();
    }

    [Fact]
    public async Task Test_MissingSettings_FailsWithClearMessage()
    {
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.SqlServer, new Dictionary<string, string>(), null, "t", DateTimeOffset.UtcNow);

        var result = await CreateConnector().TestAsync(profile, Ct);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("server");
    }

    [Theory]
    [InlineData("Orders", "[Orders]")]
    [InlineData("we]ird", "[we]]ird]")]
    [InlineData("x]; DROP TABLE t; --", "[x]]; DROP TABLE t; --]")]
    public void QuoteName_EscapesClosingBrackets(string name, string expected)
    {
        SqlServerConnector.QuoteName(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData(typeof(int), DataType.Integer)]
    [InlineData(typeof(decimal), DataType.Decimal)]
    [InlineData(typeof(DateTime), DataType.DateTime)]
    [InlineData(typeof(bool), DataType.Boolean)]
    [InlineData(typeof(Guid), DataType.String)]
    [InlineData(typeof(byte[]), DataType.String)]
    public void MapType_MapsClrTypes(Type clr, DataType expected)
    {
        SqlServerConnector.MapType(clr).ShouldBe(expected);
    }

    private static async Task SeedAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF OBJECT_ID('dbo.{TableName}') IS NOT NULL DROP TABLE dbo.{TableName};
            CREATE TABLE dbo.{TableName} (id INT NOT NULL PRIMARY KEY, region NVARCHAR(20) NULL, amount DECIMAL(18,2) NULL, ordered_on DATETIME2 NULL);
            WITH n AS (SELECT TOP ({Rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT dbo.{TableName} SELECT i, CHOOSE(i % 4 + 1, 'North', 'South', 'East', NULL), i * 1.25, DATEADD(day, i, '2025-01-01') FROM n;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
