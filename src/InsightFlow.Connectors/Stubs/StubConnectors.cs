using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;

namespace InsightFlow.Connectors.Stubs;

/// <summary>
/// Base for connectors that are registered (so the UI can list them) but not implemented yet. Each subclass's
/// summary is the implementation note for Developer 2. Implement it, then make its test class inherit
/// <c>ConnectorContractTests</c> (see docs/handoff-dev2.md, "Adding a connector").
/// </summary>
public abstract class NotImplementedConnector : IDataSourceConnector
{
    public abstract DataSourceKind Kind { get; }

    public abstract ConnectorCapabilities Capabilities { get; }

    public Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken) => throw NotYet();

    public IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, CancellationToken cancellationToken) => throw NotYet();

    public Task<ExtractResult> ExtractAsync(ExtractRequest request, IExtractWriter writer, CancellationToken cancellationToken) => throw NotYet();

    private NotImplementedException NotYet() => new($"TODO(dev2): the {Kind} connector is not implemented yet.");
}

// TODO(dev2): Excel — use ExcelDataReader (package referenced). Discover = one SourceTable per worksheet ("Sheet1");
// first row = headers; infer types from the first N rows (DataType.Decimal for numerics, Date/DateTime for OADate cells),
// then stream rows into writer.BeginTableAsync/AppendRow. Download via ISourceFileAccessor (like FileConnectorBase).
/// <summary>Excel workbooks (.xlsx / .xls) — not implemented yet.</summary>
public sealed class ExcelConnector : NotImplementedConnector
{
    public override DataSourceKind Kind => DataSourceKind.Excel;

    public override ConnectorCapabilities Capabilities => ConnectorCapabilities.FileOnly;
}

// TODO(dev2): PostgreSQL — Npgsql (referenced). Mirror SqlServerConnector: settings host/port/database/user (+ secret),
// sslmode=require by default; discover via information_schema.tables; quote with "..." ; stream with NpgsqlDataReader.
/// <summary>PostgreSQL — not implemented yet.</summary>
public sealed class PostgreSqlConnector : NotImplementedConnector
{
    public override DataSourceKind Kind => DataSourceKind.PostgreSql;

    public override ConnectorCapabilities Capabilities { get; } = new(false, false, false);
}

// TODO(dev2): MySQL — MySqlConnector (referenced). Same pattern; quote with backticks; SslMode=Required; use
// CommandBehavior.SequentialAccess for streaming.
/// <summary>MySQL — not implemented yet.</summary>
public sealed class MySqlDbConnector : NotImplementedConnector
{
    public override DataSourceKind Kind => DataSourceKind.MySql;

    public override ConnectorCapabilities Capabilities { get; } = new(false, false, false);
}

// TODO(dev2): Oracle — Oracle.ManagedDataAccess.Core (referenced). Discover via ALL_TABLES/ALL_VIEWS for the user's
// accessible schemas; map NUMBER(p,0) to Integer and NUMBER(p,s) to Decimal; set FetchSize for streaming.
/// <summary>Oracle — not implemented yet.</summary>
public sealed class OracleConnector : NotImplementedConnector
{
    public override DataSourceKind Kind => DataSourceKind.Oracle;

    public override ConnectorCapabilities Capabilities { get; } = new(false, false, false);
}

// TODO(dev2): MongoDB — MongoDB.Driver (referenced). Discover = collections. Sample ConnectorOptions.DocumentSampleSize
// documents to infer a schema: nested objects flatten to dotted columns ("address.city"), arrays become JSON text
// (DataType.Json) in v1, conflicting types widen to String. Then stream all documents with a cursor and AppendRow.
/// <summary>MongoDB — not implemented yet.</summary>
public sealed class MongoDbConnector : NotImplementedConnector
{
    public override DataSourceKind Kind => DataSourceKind.MongoDb;

    public override ConnectorCapabilities Capabilities { get; } = new(false, IsDocumentStore: true, false);
}

// TODO(dev2): Azure Cosmos DB (NoSQL API) — Microsoft.Azure.Cosmos (referenced). Discover = containers of the database
// setting; same sampling/flattening rules as MongoDB; read with a feed iterator (SELECT * FROM c) page by page; skip
// system properties (_rid, _ts, _etag, _self, _attachments). Prefer managed identity over account keys.
/// <summary>Azure Cosmos DB (NoSQL API) — not implemented yet.</summary>
public sealed class CosmosDbConnector : NotImplementedConnector
{
    public override DataSourceKind Kind => DataSourceKind.CosmosDb;

    public override ConnectorCapabilities Capabilities { get; } = new(false, IsDocumentStore: true, false);
}
