using System.Data;
using System.Runtime.CompilerServices;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Threads;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace InsightFlow.Connectors.Databases;

/// <summary>
/// MySQL / MariaDB.
/// <list type="bullet">
/// <item>Settings: <c>host</c>, <c>database</c>, <c>user</c> (password is the profile secret), optional <c>port</c>
/// (default 3306) and <c>sslmode</c> (default <c>required</c>).</item>
/// <item>Discovery lists tables and views outside the server schemas. Identifiers are backtick-quoted.</item>
/// <item>Rows stream with <see cref="CommandBehavior.SequentialAccess"/>.</item>
/// </list>
/// </summary>
public sealed class MySqlDbConnector(ISecretStore secrets, IOptions<ConnectorOptions> options) : IDataSourceConnector
{
    public DataSourceKind Kind => DataSourceKind.MySql;

    public ConnectorCapabilities Capabilities { get; } = new(SupportsLivePushdown: false, IsDocumentStore: false, SupportsIncremental: false);

    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(profile, cancellationToken);
            await using var command = new MySqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return ConnectionTestResult.Ok($"Connected to {connection.DataSource}/{connection.Database}.");
        }
        catch (ConnectorException ex)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
    }

    public async IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(profile, cancellationToken);
        await using var command = new MySqlCommand(
            """
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """,
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
        var (schema, name) = await ResolveTableAsync(connection, request.Table, request.Profile.GetSetting("database") ?? string.Empty, cancellationToken);
        var limit = request.MaxRows is { } ? " LIMIT @max" : string.Empty;
        await using var command = new MySqlCommand(
            $"SELECT * FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(name)}{limit}", connection)
        {
            CommandTimeout = (int)options.Value.SourceCommandTimeout.TotalSeconds,
        };
        if (request.MaxRows is { } max)
        {
            command.Parameters.Add(new MySqlParameter("@max", MySqlDbType.Int64) { Value = max });
        }

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var columns = reader.GetColumnSchema()
            .Select(c => new SchemaColumn(c.ColumnName, RelationalExtract.MapClrType(c.DataType), c.AllowDBNull ?? true))
            .ToList();
        return await RelationalExtract.CopyRowsAsync(reader, columns, writer, cancellationToken);
    }

    /// <summary>Builds the connection string from settings + secret. Never logged; exposed for tests.</summary>
    internal async Task<string> BuildConnectionStringAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var host = profile.GetSetting("host");
        var database = profile.GetSetting("database");
        var user = profile.GetSetting("user");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(user))
        {
            throw new ConnectorException("Settings 'host', 'database' and 'user' are required.");
        }

        if (profile.Secret is not { } secret)
        {
            throw new ConnectorException("MySQL authentication needs a password.");
        }

        var password = await secrets.GetAsync(profile.TenantId, secret, cancellationToken)
            ?? throw new ConnectorException("The stored password for this connection is missing.");
        var builder = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)RelationalExtract.ParsePort(profile.GetSetting("port"), 3306),
            Database = database,
            UserID = user,
            Password = password,
            SslMode = ParseSslMode(profile.GetSetting("sslmode")),
            ConnectionTimeout = 15,
            ApplicationName = "InsightFlow",
        };
        return builder.ConnectionString;
    }

    internal static string QuoteIdentifier(string identifier) => RelationalExtract.Quote(identifier, '`');

    private async Task<MySqlConnection> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        string? password = null;
        if (profile.Secret is { } secret)
        {
            password = await secrets.GetAsync(profile.TenantId, secret, cancellationToken);
        }

        var connection = new MySqlConnection(await BuildConnectionStringAsync(profile, cancellationToken));
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var readOnly = new MySqlCommand("SET SESSION TRANSACTION READ ONLY", connection);
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch (MySqlException ex)
        {
            await connection.DisposeAsync();
            throw new ConnectorException(RelationalExtract.SafeMessage(ex.Message, password), ex);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static MySqlSslMode ParseSslMode(string? text) => (text ?? "required").ToLowerInvariant() switch
    {
        "disable" or "none" => MySqlSslMode.None,
        "preferred" or "prefer" => MySqlSslMode.Preferred,
        "required" or "require" => MySqlSslMode.Required,
        "verify-ca" => MySqlSslMode.VerifyCA,
        "verify-full" => MySqlSslMode.VerifyFull,
        _ => throw new ConnectorException("Setting 'sslmode' must be disable, preferred, required, verify-ca or verify-full."),
    };

    private static async Task<(string Schema, string Name)> ResolveTableAsync(
        MySqlConnection connection, string table, string defaultSchema, CancellationToken ct)
    {
        var dot = table.IndexOf('.', StringComparison.Ordinal);
        var schema = dot > 0 ? table[..dot] : defaultSchema;
        var name = dot > 0 ? table[(dot + 1)..] : table;
        await using var command = new MySqlCommand(
            """
            SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name
            """,
            connection);
        command.Parameters.Add(new MySqlParameter("@schema", schema));
        command.Parameters.Add(new MySqlParameter("@name", name));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1))
            : throw new ConnectorException($"Table '{table}' was not found.");
    }
}
