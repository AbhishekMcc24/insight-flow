using System.Data;
using System.Runtime.CompilerServices;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Threads;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InsightFlow.Connectors.Databases;

/// <summary>
/// PostgreSQL.
/// <list type="bullet">
/// <item>Settings: <c>host</c>, <c>database</c>, <c>user</c> (password is the profile secret), optional <c>port</c>
/// (default 5432) and <c>sslmode</c> (default <c>require</c>, which encrypts without validating the certificate;
/// <c>verify-full</c> validates it).</item>
/// <item>Discovery lists tables and views in the current database, excluding <c>pg_catalog</c> and <c>information_schema</c>.</item>
/// <item>The requested table must be one returned by discovery and is quoted, never interpolated. Rows stream into the writer.</item>
/// </list>
/// </summary>
public sealed class PostgreSqlConnector(ISecretStore secrets, IOptions<ConnectorOptions> options) : IDataSourceConnector
{
    public DataSourceKind Kind => DataSourceKind.PostgreSql;

    public ConnectorCapabilities Capabilities { get; } = new(SupportsLivePushdown: false, IsDocumentStore: false, SupportsIncremental: false);

    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(profile, cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return ConnectionTestResult.Ok($"Connected to {connection.Host}/{connection.Database}.");
        }
        catch (ConnectorException ex)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
    }

    public async IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(profile, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT table_schema, table_name, table_type
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_name
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
        var (schema, name) = await ResolveTableAsync(connection, request.Table, cancellationToken);
        var limit = request.MaxRows is { } ? " LIMIT @max" : string.Empty;
        await using var command = new NpgsqlCommand(
            $"SELECT * FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(name)}{limit}", connection)
        {
            CommandTimeout = (int)options.Value.SourceCommandTimeout.TotalSeconds,
        };
        if (request.MaxRows is { } max)
        {
            command.Parameters.Add(new NpgsqlParameter("max", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = max });
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
            throw new ConnectorException("PostgreSQL authentication needs a password.");
        }

        var password = await secrets.GetAsync(profile.TenantId, secret, cancellationToken)
            ?? throw new ConnectorException("The stored password for this connection is missing.");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = RelationalExtract.ParsePort(profile.GetSetting("port"), 5432),
            Database = database,
            Username = user,
            Password = password,
            SslMode = ParseSslMode(profile.GetSetting("sslmode")),
            Timeout = 15,
            ApplicationName = "InsightFlow",
        };
        return builder.ConnectionString;
    }

    internal static string QuoteIdentifier(string identifier) => RelationalExtract.Quote(identifier, '"');

    private async Task<NpgsqlConnection> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        string? password = null;
        if (profile.Secret is { } secret)
        {
            password = await secrets.GetAsync(profile.TenantId, secret, cancellationToken);
        }

        var connection = new NpgsqlConnection(await BuildConnectionStringAsync(profile, cancellationToken));
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var readOnly = new NpgsqlCommand("SET default_transaction_read_only = on", connection);
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch (NpgsqlException ex)
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

    private static SslMode ParseSslMode(string? text) => (text ?? "require").ToLowerInvariant() switch
    {
        "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "require" => SslMode.Require,
        "verify-ca" => SslMode.VerifyCA,
        "verify-full" => SslMode.VerifyFull,
        _ => throw new ConnectorException("Setting 'sslmode' must be disable, allow, prefer, require, verify-ca or verify-full."),
    };

    private static async Task<(string Schema, string Name)> ResolveTableAsync(NpgsqlConnection connection, string table, CancellationToken ct)
    {
        var dot = table.IndexOf('.', StringComparison.Ordinal);
        var schema = dot > 0 ? table[..dot] : "public";
        var name = dot > 0 ? table[(dot + 1)..] : table;
        await using var command = new NpgsqlCommand(
            """
            SELECT table_schema, table_name FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @name
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("schema", schema));
        command.Parameters.Add(new NpgsqlParameter("name", name));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1))
            : throw new ConnectorException($"Table '{table}' was not found.");
    }
}
