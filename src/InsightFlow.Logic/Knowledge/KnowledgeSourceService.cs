using InsightFlow.Infrastructure.Connectors;
using InsightFlow.Infrastructure.Connectors.Databases;
using InsightFlow.Infrastructure.Connectors.Extraction;
using InsightFlow.Infrastructure.Connectors.Knowledge;
using InsightFlow.Domain.Contracts.Connections;
using InsightFlow.Domain.Contracts.Knowledge;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Repository;
using InsightFlow.Foundation.Security;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Logic.Knowledge;

/// <summary>
/// The "Import tables" explorer: test a database server (from fields or a pasted connection string), save it, then browse its
/// databases, tables and columns before importing. Only catalog metadata is read, never rows. A connection that is only being
/// tested is never stored: its password lives in the secret store for the length of the call and is deleted afterwards.
/// Sys-admin only (enforced by the endpoints).
/// </summary>
public sealed partial class KnowledgeSourceService(
    InsightFlowDbContext db,
    ITenantContext caller,
    ISecretStore secrets,
    IConnectorRegistry connectors,
    ExtractPipeline pipeline,
    IHostEnvironment environment,
    TimeProvider clock,
    ILogger<KnowledgeSourceService> logger)
{
    private static readonly DataSourceKind[] SupportedKinds = [DataSourceKind.SqlServer, DataSourceKind.PostgreSql, DataSourceKind.MySql];

    private TenantId Tenant => new(caller.TenantId ?? throw new InvalidOperationException("No tenant in scope."));

    private string UserId => caller.UserId ?? throw new InvalidOperationException("No user in scope.");

    public async Task<SourceTestResultDto> TestAsync(SourceConnectionRequest request, CancellationToken cancellationToken)
    {
        var (kind, settings, password) = Resolve(request);
        await EnsureHostAllowedAsync(kind, settings, cancellationToken);

        SecretReference? temporary = password is null ? null : await secrets.SaveAsync(Tenant, password, cancellationToken);
        try
        {
            var profile = ConnectionProfile.Create(
                Tenant, string.IsNullOrWhiteSpace(request.Name) ? "Test" : request.Name, kind, WithDefaultDatabase(kind, settings), temporary, UserId, clock.GetUtcNow());
            var connector = connectors.Resolve(kind);
            var test = await connector.TestAsync(profile, cancellationToken);
            if (!test.Success)
            {
                LogTested(logger, kind, false);
                return new SourceTestResultDto(false, test.Message, []);
            }

            var databases = connector is IDatabaseCatalog catalog ? await catalog.ListDatabasesAsync(profile, cancellationToken) : [];
            LogTested(logger, kind, true);
            return new SourceTestResultDto(true, test.Message, databases);
        }
        catch (ConnectorException ex)
        {
            LogTested(logger, kind, false);
            return new SourceTestResultDto(false, ex.Message, []);
        }
        finally
        {
            if (temporary is { } secret)
            {
                await secrets.DeleteAsync(Tenant, secret, CancellationToken.None);
            }
        }
    }

    public async Task<ConnectionDto> SaveAsync(SourceConnectionRequest request, CancellationToken cancellationToken)
    {
        var (kind, settings, password) = Resolve(request);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ApiProblemException("Give the connection a name.");
        }

        await EnsureHostAllowedAsync(kind, settings, cancellationToken);
        var secret = password is null ? (SecretReference?)null : await secrets.SaveAsync(Tenant, password, cancellationToken);
        var profile = ConnectionProfile.Create(Tenant, request.Name, kind, settings, secret, UserId, clock.GetUtcNow());
        db.ConnectionProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        return new ConnectionDto(profile.Id, profile.Name, profile.Kind.ToString(), profile.Settings, profile.Secret is not null, profile.CreatedAt);
    }

    /// <summary>
    /// Removes a saved connection and its stored password. Tables already imported into the knowledge base stay (they are
    /// independent definitions); a connection that datasets were extracted from cannot be removed.
    /// </summary>
    public async Task DeleteAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var profile = await LoadAsync(connectionId, cancellationToken);
        if (await db.ExtractDefinitions.AnyAsync(d => d.ConnectionProfileId == profile.Id, cancellationToken))
        {
            throw ApiProblemException.Conflict("Datasets were created from this connection, so it cannot be removed.");
        }

        db.ConnectionProfiles.Remove(profile);
        await db.SaveChangesAsync(cancellationToken);
        if (profile.Secret is { } secret)
        {
            await secrets.DeleteAsync(Tenant, secret, cancellationToken);
        }

        LogDeleted(logger, profile.Kind);
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var profile = await LoadAsync(connectionId, cancellationToken);
        var connector = connectors.Resolve(profile.Kind);
        if (connector is not IDatabaseCatalog catalog)
        {
            return string.IsNullOrEmpty(profile.GetSetting("database")) ? [] : [profile.GetSetting("database")!];
        }

        await EnsureHostAllowedAsync(profile.Kind, profile.Settings, cancellationToken);
        return await catalog.ListDatabasesAsync(WithDatabase(profile, null), cancellationToken);
    }

    public async Task<IReadOnlyList<SourceTableDto>> ListTablesAsync(Guid connectionId, string? database, CancellationToken cancellationToken)
    {
        var profile = WithDatabase(await LoadAsync(connectionId, cancellationToken), database);
        await EnsureHostAllowedAsync(profile.Kind, profile.Settings, cancellationToken);
        var tables = new List<SourceTableDto>();
        await foreach (var table in connectors.Resolve(profile.Kind).DiscoverAsync(profile, cancellationToken))
        {
            tables.Add(new SourceTableDto(table.Id, table.Name, table.Schema, table.Kind));
        }

        return tables;
    }

    public async Task<SourceSchemaDto> DescribeAsync(Guid connectionId, SourceDescribeRequest request, CancellationToken cancellationToken)
    {
        if (request.TableIds is null || request.TableIds.Count == 0)
        {
            throw new ApiProblemException("Choose a table to preview.");
        }

        var profile = WithDatabase(await LoadAsync(connectionId, cancellationToken), request.Database);
        if (connectors.Resolve(profile.Kind) is not ISchemaDescriber describer)
        {
            throw new ApiProblemException(StatusCodes.Status422UnprocessableEntity, "not_supported", $"Reading table definitions from {profile.Kind} is not supported yet.");
        }

        await EnsureHostAllowedAsync(profile.Kind, profile.Settings, cancellationToken);
        var schema = await describer.DescribeAsync(profile, request.TableIds, cancellationToken);
        return new SourceSchemaDto(
            schema.Tables.Select(t => new SourceTableDefinitionDto(
                t.Table.Id, t.Table.Name, t.Table.Schema, t.Table.Kind,
                t.Columns.Select(c => new SourceColumnDto(c.Name, c.DataType, c.IsNullable, c.IsPrimaryKey)).ToList())).ToList(),
            schema.ForeignKeys.Select(f => new SourceForeignKeyDto(f.FromTable, f.FromColumn, f.ToTable, f.ToColumn)).ToList());
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Turns a request (fields or connection string) into the connector settings and the password.</summary>
    private static (DataSourceKind Kind, Dictionary<string, string> Settings, string? Password) Resolve(SourceConnectionRequest request)
    {
        if (!Enum.TryParse<DataSourceKind>(request.Kind, ignoreCase: true, out var kind) || !SupportedKinds.Contains(kind))
        {
            throw new ApiProblemException("Choose SQL Server, PostgreSQL or MySQL.");
        }

        try
        {
            if (string.Equals(request.Mode, "connectionString", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = SourceConnectionStrings.Parse(kind, request.ConnectionString ?? string.Empty);
                return (kind, new Dictionary<string, string>(parsed.Settings, StringComparer.OrdinalIgnoreCase), parsed.Password);
            }
        }
        catch (ConnectorException ex)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_connection_string", ex.Message);
        }

        var host = request.Host?.Trim();
        if (string.IsNullOrEmpty(host) || string.IsNullOrWhiteSpace(request.User))
        {
            throw new ApiProblemException(kind == DataSourceKind.SqlServer ? "Enter the server and the user." : "Enter the host and the user.");
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["user"] = request.User.Trim() };
        if (!string.IsNullOrWhiteSpace(request.Database))
        {
            settings["database"] = request.Database.Trim();
        }

        if (kind == DataSourceKind.SqlServer)
        {
            settings["server"] = !string.IsNullOrWhiteSpace(request.Port) && !host.Contains(',', StringComparison.Ordinal) ? $"{host},{request.Port.Trim()}" : host;
            if (request.TrustCertificate)
            {
                settings["trustServerCertificate"] = "true";
            }
        }
        else
        {
            settings["host"] = host;
            settings["sslmode"] = string.IsNullOrWhiteSpace(request.SslMode) ? (kind == DataSourceKind.PostgreSql ? "require" : "required") : request.SslMode.Trim();
            if (!string.IsNullOrWhiteSpace(request.Port))
            {
                settings["port"] = request.Port.Trim();
            }
        }

        return (kind, settings, string.IsNullOrEmpty(request.Password) ? null : request.Password);
    }

    /// <summary>The server's default database, for listing databases before one has been chosen.</summary>
    private static Dictionary<string, string> WithDefaultDatabase(DataSourceKind kind, IReadOnlyDictionary<string, string> settings)
    {
        var copy = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase);
        if (!copy.TryGetValue("database", out var db) || string.IsNullOrWhiteSpace(db))
        {
            copy["database"] = kind switch { DataSourceKind.SqlServer => "master", DataSourceKind.PostgreSql => "postgres", _ => "mysql" };
        }

        return copy;
    }

    /// <summary>The saved connection pointed at another database on the same server (same credentials), or at its default database.</summary>
    private ConnectionProfile WithDatabase(ConnectionProfile profile, string? database)
    {
        var settings = WithDefaultDatabase(profile.Kind, profile.Settings);
        if (!string.IsNullOrWhiteSpace(database))
        {
            settings["database"] = database.Trim();
        }

        return ConnectionProfile.Create(profile.TenantId, profile.Name, profile.Kind, settings, profile.Secret, UserId, clock.GetUtcNow());
    }

    private async Task<ConnectionProfile> LoadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var profile = await db.ConnectionProfiles.SingleOrDefaultAsync(c => c.Id == connectionId, cancellationToken) ?? throw ApiProblemException.NotFound("The connection");
        return SupportedKinds.Contains(profile.Kind) ? profile : throw new ApiProblemException($"{profile.Kind} connections cannot be explored here.");
    }

    /// <summary>In production a tenant administrator must not be able to aim the server at private addresses.</summary>
    private Task EnsureHostAllowedAsync(DataSourceKind kind, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        var raw = (kind == DataSourceKind.SqlServer ? settings.GetValueOrDefault("server") : settings.GetValueOrDefault("host")) ?? string.Empty;
        var host = raw.Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        host = host.Split(',', '\\')[0].Trim();
        if (kind != DataSourceKind.SqlServer)
        {
            host = host.Split(',')[0].Trim();
        }

        return KnowledgeEndpointPolicy.EnsureAllowedAsync($"tcp://{host}", environment.IsDevelopment(), cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Source connection ({Kind}) removed")]
    private static partial void LogDeleted(ILogger logger, DataSourceKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Source connection test ({Kind}) succeeded: {Succeeded}")]
    private static partial void LogTested(ILogger logger, DataSourceKind kind, bool succeeded);
}
