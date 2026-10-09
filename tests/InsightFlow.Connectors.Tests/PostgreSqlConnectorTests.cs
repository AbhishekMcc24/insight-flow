using InsightFlow.Connectors.Databases;
using InsightFlow.Domain.Connections;
using InsightFlow.Testing;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InsightFlow.Connectors.Tests;

/// <summary>
/// PostgreSQL contract tests. Set <c>INSIGHTFLOW_TEST_POSTGRES</c> to a connection string of a database where the
/// tests may create <c>public.insightflow_contract_orders</c>. Without it the contract tests are skipped.
/// </summary>
public sealed class PostgreSqlConnectorTests : ConnectorContractTests
{
    public const string EnvironmentVariable = "INSIGHTFLOW_TEST_POSTGRES";
    private const string TableName = "insightflow_contract_orders";
    private const int Rows = 250;

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private static readonly Lazy<Task> SeedOnce = new(SeedAsync);
    private readonly InMemorySecretStore _secrets = new();

    protected override DataSourceKind ExpectedKind => DataSourceKind.PostgreSql;

    protected override string ExpectedTableId => $"public.{TableName}";

    protected override long ExpectedRowCount => Rows;

    protected override IReadOnlyCollection<string> ExpectedColumns => ["id", "region", "amount", "ordered_on"];

    protected override IDataSourceConnector CreateConnector() =>
        new PostgreSqlConnector(_secrets, Options.Create(new ConnectorOptions()));

    protected override void SkipIfUnavailable() =>
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ConnectionString), $"Set {EnvironmentVariable} to run PostgreSQL contract tests.");

    protected override async Task<ConnectionProfile> CreateValidProfileAsync()
    {
        await SeedOnce.Value;
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString);
        var secret = await _secrets.SaveAsync(RetailModel.Tenant, csb.Password ?? string.Empty, Ct);
        return ConnectionProfile.Create(RetailModel.Tenant, "Contract test", DataSourceKind.PostgreSql, new Dictionary<string, string>
        {
            ["host"] = csb.Host!,
            ["port"] = csb.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["database"] = csb.Database!,
            ["user"] = csb.Username!,
            ["sslmode"] = "disable",
        }, secret, "test", DateTimeOffset.UtcNow);
    }

    protected override ConnectionProfile CreateUnreachableProfile()
    {
        var secret = _secrets.SaveAsync(RetailModel.Tenant, "wrong-password", Ct).GetAwaiter().GetResult();
        return ConnectionProfile.Create(RetailModel.Tenant, "Unreachable", DataSourceKind.PostgreSql, new Dictionary<string, string>
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
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.PostgreSql, new Dictionary<string, string>
        {
            ["host"] = "db.example.com",
            ["database"] = "sales",
            ["user"] = "reader",
        }, secret, "t", DateTimeOffset.UtcNow);

        var csb = new NpgsqlConnectionStringBuilder(await ((PostgreSqlConnector)CreateConnector()).BuildConnectionStringAsync(profile, Ct));

        csb.Password.ShouldBe("s3cr3t!");
        csb.SslMode.ShouldBe(SslMode.Require);
        profile.Settings.Values.ShouldNotContain("s3cr3t!");
    }

    [Fact]
    public async Task Test_MissingSettings_FailsWithClearMessage()
    {
        var profile = ConnectionProfile.Create(RetailModel.Tenant, "c", DataSourceKind.PostgreSql, new Dictionary<string, string>(), null, "t", DateTimeOffset.UtcNow);
        var result = await CreateConnector().TestAsync(profile, Ct);
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("host");
    }

    [Theory]
    [InlineData("Orders", "\"Orders\"")]
    [InlineData("we\"ird", "\"we\"\"ird\"")]
    public void QuoteIdentifier_EscapesQuotes(string name, string expected) =>
        PostgreSqlConnector.QuoteIdentifier(name).ShouldBe(expected);

    private static async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            DROP TABLE IF EXISTS public.{TableName};
            CREATE TABLE public.{TableName} (id INT PRIMARY KEY, region TEXT NULL, amount NUMERIC(18,2) NULL, ordered_on TIMESTAMP NULL);
            INSERT INTO public.{TableName}
            SELECT i,
                   (ARRAY['North','South','East',NULL])[1 + (i % 4)],
                   i * 1.25,
                   TIMESTAMP '2025-01-01' + (i || ' days')::interval
            FROM generate_series(1, {Rows}) AS i;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }
}
