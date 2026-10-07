using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Connectors;

/// <summary>What a connector can do beyond extracting to Parquet.</summary>
/// <param name="SupportsLivePushdown">Queries could run on the source directly (future; v1 always extracts).</param>
/// <param name="IsDocumentStore">Schema is inferred from sampled documents and nested data is flattened.</param>
/// <param name="SupportsIncremental">Only new/changed rows can be extracted (future).</param>
public sealed record ConnectorCapabilities(bool SupportsLivePushdown, bool IsDocumentStore, bool SupportsIncremental)
{
    public static ConnectorCapabilities FileOnly { get; } = new(false, false, false);
}

/// <summary>Outcome of <see cref="IDataSourceConnector.TestAsync"/>. The message is user-safe (no secrets).</summary>
public sealed record ConnectionTestResult(bool Success, string Message)
{
    public static ConnectionTestResult Ok(string message = "Connection succeeded.") => new(true, message);

    public static ConnectionTestResult Failed(string message) => new(false, message);
}

/// <summary>A table, view, collection or file a connector can extract. <see cref="Id"/> is what <see cref="ExtractRequest.Table"/> must contain.</summary>
public sealed record SourceTable(string Id, string Name, string? Schema = null, string Kind = "table");

/// <summary>What to extract: a profile (saved or transient for uploaded files), the table id from discovery, and an optional row cap.</summary>
public sealed record ExtractRequest(ConnectionProfile Profile, string? Table = null, long? MaxRows = null)
{
    public TenantId Tenant => Profile.TenantId;
}

/// <summary>Connector-level outcome; the schema and row count come from the writer.</summary>
public sealed record ExtractResult(long RowsRead, IReadOnlyList<string> Warnings)
{
    public static ExtractResult Rows(long rows) => new(rows, []);
}

/// <summary>
/// A source system adapter. Connectors stream rows into an <see cref="IExtractWriter"/> — they never buffer a whole
/// source in memory — and resolve credentials through <c>ISecretStore</c> only when connecting. Every implementation
/// inherits the contract test suite in <c>InsightFlow.Connectors.Tests</c>.
/// </summary>
public interface IDataSourceConnector
{
    DataSourceKind Kind { get; }

    ConnectorCapabilities Capabilities { get; }

    Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken);

    IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, CancellationToken cancellationToken);

    /// <exception cref="ConnectorException">The source rejected the request (unknown table, bad credentials…).</exception>
    Task<ExtractResult> ExtractAsync(ExtractRequest request, IExtractWriter writer, CancellationToken cancellationToken);
}

/// <summary>Finds the connector for a <see cref="DataSourceKind"/>.</summary>
public interface IConnectorRegistry
{
    IDataSourceConnector Resolve(DataSourceKind kind);

    IReadOnlyCollection<DataSourceKind> Kinds { get; }
}

/// <summary>A user-presentable connector failure. <see cref="Exception.Message"/> must never contain secrets or row data.</summary>
public sealed class ConnectorException : Exception
{
    public ConnectorException()
    {
    }

    public ConnectorException(string message)
        : base(message)
    {
    }

    public ConnectorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class ConnectorRegistry(IEnumerable<IDataSourceConnector> connectors) : IConnectorRegistry
{
    private readonly Dictionary<DataSourceKind, IDataSourceConnector> _byKind = connectors.ToDictionary(c => c.Kind);

    public IReadOnlyCollection<DataSourceKind> Kinds => _byKind.Keys;

    public IDataSourceConnector Resolve(DataSourceKind kind) =>
        _byKind.TryGetValue(kind, out var connector) ? connector : throw new NotSupportedException($"No connector is registered for {kind}.");
}
