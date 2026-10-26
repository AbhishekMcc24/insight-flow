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
public sealed class SqlServerConnector(ISecretStore secrets, IOptions<ConnectorOptions> options) : IDataSourceConnector, ISchemaDescriber, IDatabaseCatalog, IProcedureCatalog
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

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(profile, cancellationToken);
        await using var command = new SqlCommand(
            "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 AND database_id > 4 ORDER BY name",
            connection);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        // Azure SQL shows only the current database; a server login without access to user databases still has the one it connected to.
        return names.Count > 0 ? names : [connection.Database];
    }

    /// <summary>Stored procedures of the connected database with their source. Fixed query; at most 2,000 procedures, each cut at 20,000 characters.</summary>
    public async Task<IReadOnlyList<SourceProcedure>> ListProceduresAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(profile, cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT TOP (2000) s.name, o.name, LEFT(m.definition, 20000)
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type = 'P' AND o.is_ms_shipped = 0 AND m.definition IS NOT NULL
            ORDER BY s.name, o.name
            """,
            connection)
        {
            CommandTimeout = (int)options.Value.SourceCommandTimeout.TotalSeconds,
        };
        var procedures = new List<SourceProcedure>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            procedures.Add(new SourceProcedure(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return procedures;
    }

    public async Task<SourceSchema> DescribeAsync(ConnectionProfile profile, IReadOnlyCollection<string>? tableIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(profile, cancellationToken);

        var columns = new List<(string, string, string, SourceColumn)>();
        await using (var command = new SqlCommand(
            """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, t.TABLE_TYPE, c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = SchemaAssembly.TypeWithLength(reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetValue(5));
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2) == "VIEW" ? "view" : "table",
                    new SourceColumn(reader.GetString(3), type, reader.GetString(6) == "YES", false)));
            }
        }

        var primaryKeys = new List<(string, string, string)>();
        await using (var command = new SqlCommand(
            """
            SELECT k.TABLE_SCHEMA, k.TABLE_NAME, k.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
              ON k.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND k.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                primaryKeys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var foreignKeys = new List<SourceForeignKey>();
        await using (var command = new SqlCommand(
            """
            SELECT OBJECT_SCHEMA_NAME(fkc.parent_object_id), OBJECT_NAME(fkc.parent_object_id), pc.name,
                   OBJECT_SCHEMA_NAME(fkc.referenced_object_id), OBJECT_NAME(fkc.referenced_object_id), rc.name
            FROM sys.foreign_key_columns fkc
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                foreignKeys.Add(new SourceForeignKey(
                    $"{reader.GetString(0)}.{reader.GetString(1)}", reader.GetString(2), $"{reader.GetString(3)}.{reader.GetString(4)}", reader.GetString(5)));
            }
        }

        return SchemaAssembly.Assemble(columns, primaryKeys, foreignKeys, tableIds);
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
