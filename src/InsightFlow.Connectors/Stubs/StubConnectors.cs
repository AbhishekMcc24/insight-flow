using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;

namespace InsightFlow.Connectors.Stubs;

/// <summary>
/// Base for a connector that is registered (so the product can list it) but not implemented yet.
/// The Api turns this into a 501. Every v1 source has a real connector; keep the type for the next one.
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
