using System.Data;
using System.Runtime.CompilerServices;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Threads;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace InsightFlow.Connectors.Databases;

/// <summary>
/// Microsoft SQL Server / Azure SQL (reference implementation for database connectors).
/// <list type="bullet">
/// <item>Settings: <c>server</c>, <c>database</c>, <c>user</c> (SQL authentication; the password is the profile's secret),
/// or <c>authentication = ActiveDirectoryDefault</c> for Entra ID / managed identity; optional <c>encrypt</c> (default true)
/// and <c>trustServerCertificate</c> (default false).</item>
/// <item>Connections are read-only intent; discovery lists tables and views; extraction streams with
/// <see cref="CommandBehavior.SequentialAccess"/> into the writer, so memory stays flat regardless of table size.</item>
/// <item>The requested table must be one returned by discovery and is quoted, never interpolated.</item>
/// </list>
/// </summary>
public sealed class SqlServerConnector(ISecretStore secrets, IOptions<ConnectorOptions> options) : IDataSourceConnector
{
    public DataSourceKind Kind => DataSourceKind.SqlServer;

    public ConnectorCapabilities Capabilities { get; } = new(SupportsLivePushdown: false, IsDocumentStore: false, SupportsIncremental: false);

    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(profile, cancellationToken);
            await using var command = new SqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return ConnectionTestResult.Ok($"Connected to {connection.DataSource}/{connection.Database}.");
        }
        catch (ConnectorException ex)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
        catch (SqlException ex)
        {
            // SqlException messages describe the failure (login failed, server not found) and never echo the password.
            return ConnectionTestResult.Failed($"SQL Server error {ex.Number}: {ex.Message}");
        }
    }

    public async IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(profile, cancellationToken);
        await using var command = new SqlCommand(
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            yield return new SourceTable($"{schema}.{name}", name, schema, reader.GetString(2) == "VIEW" ? "view" : "table");
        }
    }

    public async Task<ExtractResult> ExtractAsync(ExtractRequest request, IExtractWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        if (string.IsNullOrWhiteSpace(request.Table))
        {
            throw new ConnectorException("A table is required.");
        }

        await using var connection = await OpenAsync(request.Profile, cancellationToken);
        var (schema, name) = await ResolveTableAsync(connection, request.Table, cancellationToken);

        var top = request.MaxRows is { } max ? "TOP (@max) " : string.Empty;
        await using var command = new SqlCommand($"SELECT {top}* FROM {QuoteName(schema)}.{QuoteName(name)}", connection)
        {
            CommandTimeout = (int)options.Value.SourceCommandTimeout.TotalSeconds,
        };
        if (request.MaxRows is { } limit)
        {
            command.Parameters.Add(new SqlParameter("@max", SqlDbType.BigInt) { Value = limit });
        }

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var columns = reader.GetColumnSchema()
            .Select(c => new SchemaColumn(c.ColumnName, MapType(c.DataType), c.AllowDBNull ?? true))
            .ToList();
        await writer.BeginTableAsync(columns, cancellationToken);

        var values = new object?[columns.Count];
        long rows = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
            }

            writer.AppendRow(values);
            rows++;
        }

        return ExtractResult.Rows(rows);
    }

    /// <summary>Builds the connection string from settings + secret. Never logged; exposed for tests.</summary>
    internal async Task<string> BuildConnectionStringAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var server = profile.GetSetting("server");
        var database = profile.GetSetting("database");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
        {
            throw new ConnectorException("Settings 'server' and 'database' are required.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            Encrypt = !string.Equals(profile.GetSetting("encrypt"), "false", StringComparison.OrdinalIgnoreCase)
                ? SqlConnectionEncryptOption.Mandatory
                : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = string.Equals(profile.GetSetting("trustServerCertificate"), "true", StringComparison.OrdinalIgnoreCase),
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "InsightFlow",
            ConnectTimeout = 15,
        };

        if (string.Equals(profile.GetSetting("authentication"), "ActiveDirectoryDefault", StringComparison.OrdinalIgnoreCase))
        {
            builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
        }
        else
        {
            var user = profile.GetSetting("user");
            if (string.IsNullOrWhiteSpace(user) || profile.Secret is not { } secret)
            {
                throw new ConnectorException("SQL authentication needs a 'user' setting and a password.");
            }

            builder.UserID = user;
            builder.Password = await secrets.GetAsync(profile.TenantId, secret, cancellationToken)
                ?? throw new ConnectorException("The stored password for this connection is missing.");
        }

        return builder.ConnectionString;
    }

    internal static DataType MapType(Type? type) => type switch
    {
        _ when type == typeof(long) || type == typeof(int) || type == typeof(short) || type == typeof(byte) => DataType.Integer,
        _ when type == typeof(decimal) || type == typeof(double) || type == typeof(float) => DataType.Decimal,
        _ when type == typeof(bool) => DataType.Boolean,
        _ when type == typeof(DateOnly) => DataType.Date,
        _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => DataType.DateTime,
        _ => DataType.String,
    };

    internal static string QuoteName(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private async Task<SqlConnection> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(await BuildConnectionStringAsync(profile, cancellationToken));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Only tables that exist (looked up with parameters) can be extracted.</summary>
    private static async Task<(string Schema, string Name)> ResolveTableAsync(SqlConnection connection, string table, CancellationToken ct)
    {
        var dot = table.IndexOf('.', StringComparison.Ordinal);
        var schema = dot > 0 ? table[..dot] : "dbo";
        var name = dot > 0 ? table[(dot + 1)..] : table;

        await using var command = new SqlCommand(
            "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name",
            connection);
        command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = name });
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1))
            : throw new ConnectorException($"Table '{table}' was not found.");
    }
}
