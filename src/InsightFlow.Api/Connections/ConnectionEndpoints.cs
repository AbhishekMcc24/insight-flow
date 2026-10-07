using InsightFlow.Api.Workspace;
using InsightFlow.Connectors;
using InsightFlow.Connectors.Stubs;
using InsightFlow.Contracts.Connections;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Api.Connections;

/// <summary>
/// <c>/api/v1/connections</c>: saved source connections (the secret goes to the secret store, never to the database
/// or a response), connection tests, table discovery and queuing extracts. Requires the Creator role.
/// TODO(dev2): update/delete connections, rotate secrets, schedule refreshes (cron on ExtractDefinition).
/// </summary>
internal static class ConnectionEndpoints
{
    public static RouteGroupBuilder MapConnectionEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanCreate);

        group.MapGet("/", async (InsightFlowDbContext db, CancellationToken ct) =>
        {
            var connections = await db.ConnectionProfiles.OrderBy(c => c.Name).ToListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<ConnectionDto>>(connections.Select(ToDto).ToList());
        }).WithName("ListConnections");

        group.MapPost("/", CreateAsync).WithName("CreateConnection");

        group.MapPost("/{connectionId:guid}/test", async (Guid connectionId, InsightFlowDbContext db, IConnectorRegistry connectors, CancellationToken ct) =>
        {
            var connection = await LoadAsync(db, connectionId, ct);
            var result = await connectors.Resolve(connection.Kind).TestAsync(connection, ct);
            return TypedResults.Ok(new ConnectionTestResponse(result.Success, result.Message));
        }).WithName("TestConnection");

        group.MapGet("/{connectionId:guid}/tables", async (Guid connectionId, InsightFlowDbContext db, IConnectorRegistry connectors, CancellationToken ct) =>
        {
            var connection = await LoadAsync(db, connectionId, ct);
            var tables = new List<SourceTableDto>();
            await foreach (var table in connectors.Resolve(connection.Kind).DiscoverAsync(connection, ct))
            {
                tables.Add(new SourceTableDto(table.Id, table.Name, table.Schema, table.Kind));
            }

            return TypedResults.Ok<IReadOnlyList<SourceTableDto>>(tables);
        }).WithName("DiscoverTables");

        group.MapPost("/{connectionId:guid}/extracts", async (
            Guid connectionId, CreateExtractRequest request, InsightFlowDbContext db, WorkspaceService ws, CancellationToken ct) =>
        {
            var connection = await LoadAsync(db, connectionId, ct);
            var queued = await ws.QueueExtractFromConnectionAsync(connection, request.Table, request.FolderId, request.Name, ct);
            return TypedResults.Accepted($"/api/v1/extract-runs/{queued.RunId}", queued);
        }).WithName("QueueExtract");

        return group;
    }

    private static async Task<IResult> CreateAsync(
        CreateConnectionRequest request,
        InsightFlowDbContext db,
        ISecretStore secrets,
        IConnectorRegistry connectors,
        ITenantContext caller,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!Enum.TryParse<DataSourceKind>(request.Kind, ignoreCase: true, out var kind) || kind is DataSourceKind.Csv or DataSourceKind.Excel or DataSourceKind.Parquet)
        {
            return TypedResults.Problem($"'{request.Kind}' is not a database source kind.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (connectors.Resolve(kind) is NotImplementedConnector)
        {
            return TypedResults.Problem($"{kind} connections are not supported yet.", statusCode: StatusCodes.Status501NotImplemented);
        }

        var tenant = new TenantId(caller.TenantId!.Value);
        var secret = string.IsNullOrEmpty(request.Secret) ? (SecretReference?)null : await secrets.SaveAsync(tenant, request.Secret, ct);
        var connection = ConnectionProfile.Create(tenant, request.Name, kind, request.Settings, secret, caller.UserId!, clock.GetUtcNow());
        db.ConnectionProfiles.Add(connection);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/connections/{connection.Id}", ToDto(connection));
    }

    private static async Task<ConnectionProfile> LoadAsync(InsightFlowDbContext db, Guid id, CancellationToken ct) =>
        await db.ConnectionProfiles.FindAsync([id], ct) ?? throw ApiProblemException.NotFound("The connection");

    private static ConnectionDto ToDto(ConnectionProfile c) =>
        new(c.Id, c.Name, c.Kind.ToString(), c.Settings, c.Secret is not null, c.CreatedAt);
}

/// <summary><c>/api/v1/extract-runs/{id}</c>: status of a queued extract (the Web polls this after "Create dataset").</summary>
internal static class ExtractRunEndpoints
{
    public static RouteGroupBuilder MapExtractRunEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanView);
        group.MapGet("/{runId:guid}", (Guid runId, WorkspaceService ws, CancellationToken ct) => ws.GetRunAsync(runId, ct)).WithName("GetExtractRun");
        return group;
    }
}
