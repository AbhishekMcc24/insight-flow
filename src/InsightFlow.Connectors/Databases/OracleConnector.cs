using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Threads;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace InsightFlow.Connectors.Databases;

/// <summary>
/// Oracle Database.
/// <list type="bullet">
/// <item>Settings: <c>user</c> (password is the profile secret) and either <c>dataSource</c> (EZ connect / TNS)
/// or <c>host</c> + <c>service</c> with optional <c>port</c> (default 1521).</item>
/// <item>Discovery lists tables and views the user can see, excluding the built-in system schemas.</item>
/// <item><c>NUMBER</c> with scale 0 maps to <see cref="DataType.Integer"/>; other numbers map to <see cref="DataType.Decimal"/>.
/// <c>FetchSize</c> keeps the read streaming.</item>
/// </list>
/// </summary>
public sealed class OracleConnector(ISecretStore secrets, IOptions<ConnectorOptions> options) : IDataSourceConnector
{
    private const string DiscoverSql =
        """
        SELECT OWNER, OBJECT_NAME, OBJECT_KIND FROM (
            SELECT OWNER, TABLE_NAME AS OBJECT_NAME, 'table' AS OBJECT_KIND FROM ALL_TABLES
            UNION ALL
            SELECT OWNER, VIEW_NAME, 'view' FROM ALL_VIEWS
        ) src
        WHERE OWNER NOT IN ('SYS','SYSTEM','XDB','CTXSYS','MDSYS','OLAPSYS','ORDSYS','OUTLN','DBSNMP','APPQOSSYS','WMSYS')
        ORDER BY OWNER, OBJECT_NAME
        """;

    public DataSourceKind Kind => DataSourceKind.Oracle;

    public ConnectorCapabilities Capabilities { get; } = new(SupportsLivePushdown: false, IsDocumentStore: false, SupportsIncremental: false);

    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(profile, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM DUAL";
            await command.ExecuteScalarAsync(cancellationToken);
            return ConnectionTestResult.Ok($"Connected to {connection.DataSource}.");
        }
        catch (ConnectorException ex)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
    }

    public async IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(profile, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = DiscoverSql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var owner = reader.GetString(0);
            var name = reader.GetString(1);
            yield return new SourceTable($"{owner}.{name}", name, owner, reader.GetString(2));
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
        var (owner, name) = await ResolveTableAsync(connection, request.Table, request.Profile.GetSetting("user") ?? string.Empty, cancellationToken);
        var cap = request.MaxRows is { } ? " WHERE ROWNUM <= :max" : string.Empty;
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(owner)}.{QuoteIdentifier(name)}{cap}";
        command.CommandTimeout = (int)options.Value.SourceCommandTimeout.TotalSeconds;
        command.FetchSize = 1024 * 1024;
        if (request.MaxRows is { } max)
        {
            command.Parameters.Add(new OracleParameter("max", OracleDbType.Int64) { Value = max });
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = ReadColumns(reader);
        return await RelationalExtract.CopyRowsAsync(reader, columns, writer, cancellationToken);
    }

    /// <summary>Builds the connection string from settings + secret. Never logged; exposed for tests.</summary>
    internal async Task<string> BuildConnectionStringAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var user = profile.GetSetting("user");
        if (string.IsNullOrWhiteSpace(user) || profile.Secret is not { } secret)
        {
            throw new ConnectorException("Oracle authentication needs a 'user' setting and a password.");
        }

        var dataSource = profile.GetSetting("dataSource");
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            var host = profile.GetSetting("host");
            var service = profile.GetSetting("service");
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(service))
            {
                throw new ConnectorException("Settings 'host' and 'service' are required (or set 'dataSource').");
            }

            var port = RelationalExtract.ParsePort(profile.GetSetting("port"), 1521);
            dataSource = $"//{host}:{port.ToString(CultureInfo.InvariantCulture)}/{service}";
        }

        var password = await secrets.GetAsync(profile.TenantId, secret, cancellationToken)
            ?? throw new ConnectorException("The stored password for this connection is missing.");
        var builder = new OracleConnectionStringBuilder
        {
            DataSource = dataSource,
            UserID = user,
            Password = password,
            ConnectionTimeout = 15,
        };
        return builder.ConnectionString;
    }

    internal static string QuoteIdentifier(string identifier) => RelationalExtract.Quote(identifier, '"');

    private async Task<OracleConnection> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        string? password = null;
        if (profile.Secret is { } secret)
        {
            password = await secrets.GetAsync(profile.TenantId, secret, cancellationToken);
        }

        var connection = new OracleConnection(await BuildConnectionStringAsync(profile, cancellationToken));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (OracleException ex)
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

    private static List<SchemaColumn> ReadColumns(OracleDataReader reader)
    {
        var schema = reader.GetSchemaTable() ?? throw new ConnectorException("The source returned no columns.");
        var columns = new List<SchemaColumn>(schema.Rows.Count);
        foreach (DataRow row in schema.Rows)
        {
            var name = Convert.ToString(row["ColumnName"], CultureInfo.InvariantCulture) ?? string.Empty;
            var nullable = row.Table.Columns.Contains("AllowDBNull") && row["AllowDBNull"] is not false;
            columns.Add(new SchemaColumn(name, MapColumn(row), nullable));
        }

        return columns;
    }

    private static DataType MapColumn(DataRow column)
    {
        var type = column["DataType"] as Type;
        if (type == typeof(decimal)
            && column.Table.Columns.Contains("NumericScale")
            && column["NumericScale"] is not DBNull
            && Convert.ToInt32(column["NumericScale"], CultureInfo.InvariantCulture) == 0)
        {
            return DataType.Integer;
        }

        return RelationalExtract.MapClrType(type);
    }

    private static async Task<(string Owner, string Name)> ResolveTableAsync(
        OracleConnection connection, string table, string defaultOwner, CancellationToken ct)
    {
        var dot = table.IndexOf('.', StringComparison.Ordinal);
        var owner = dot > 0 ? table[..dot] : defaultOwner;
        var name = dot > 0 ? table[(dot + 1)..] : table;
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText =
            """
            SELECT OWNER, OBJECT_NAME FROM (
                SELECT OWNER, TABLE_NAME AS OBJECT_NAME FROM ALL_TABLES
                UNION ALL
                SELECT OWNER, VIEW_NAME FROM ALL_VIEWS
            ) src
            WHERE UPPER(OWNER) = UPPER(:owner) AND UPPER(OBJECT_NAME) = UPPER(:name)
            """;
        command.Parameters.Add(new OracleParameter("owner", owner));
        command.Parameters.Add(new OracleParameter("name", name));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1))
            : throw new ConnectorException($"Table '{table}' was not found.");
    }
}
