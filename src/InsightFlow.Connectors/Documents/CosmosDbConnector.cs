using System.Globalization;
using System.Runtime.CompilerServices;
using Azure.Identity;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Security;
using DataType = InsightFlow.Domain.Modeling.DataType;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace InsightFlow.Connectors.Documents;

/// <summary>
/// Azure Cosmos DB (NoSQL API). Settings: <c>endpoint</c>, <c>database</c>, and either
/// <c>authentication=ManagedIdentity</c> or an account key stored as the profile secret.
/// Discovery lists containers. System properties (<c>_rid</c>, <c>_ts</c>, <c>_etag</c>, <c>_self</c>, <c>_attachments</c>)
/// are skipped. Schema sampling matches <see cref="MongoDbConnector"/>.
/// </summary>
public sealed class CosmosDbConnector(ISecretStore secrets, IOptions<ConnectorOptions> options) : IDataSourceConnector
{
    private static readonly HashSet<string> SystemProperties = new(StringComparer.Ordinal)
    {
        "_rid", "_ts", "_etag", "_self", "_attachments",
    };

    public DataSourceKind Kind => DataSourceKind.CosmosDb;

    public ConnectorCapabilities Capabilities { get; } = new(SupportsLivePushdown: false, IsDocumentStore: true, SupportsIncremental: false);

    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await OpenAsync(profile, cancellationToken);
            var account = await client.ReadAccountAsync();
            return ConnectionTestResult.Ok($"Connected to {account.Id}.");
        }
        catch (ConnectorException ex)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
    }

    public async IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = await OpenAsync(profile, cancellationToken);
        var database = client.GetDatabase(Required(profile, "database"));
        using var iterator = database.GetContainerQueryIterator<ContainerProperties>();
        while (iterator.HasMoreResults)
        {
            foreach (var container in await iterator.ReadNextAsync(cancellationToken))
            {
                yield return new SourceTable(container.Id, container.Id, Kind: "container");
            }
        }
    }

    public async Task<ExtractResult> ExtractAsync(ExtractRequest request, IExtractWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        if (string.IsNullOrWhiteSpace(request.Table))
        {
            throw new ConnectorException("A container is required.");
        }

        using var client = await OpenAsync(request.Profile, cancellationToken);
        var databaseName = Required(request.Profile, "database");
        if (!await ContainerExistsAsync(client, databaseName, request.Table, cancellationToken))
        {
            throw new ConnectorException($"Table '{request.Table}' was not found.");
        }

        var container = client.GetContainer(databaseName, request.Table);
        return await DocumentRows.WriteAsync(
            writer,
            Math.Max(1, options.Value.DocumentSampleSize),
            ReadAsync(container, request.MaxRows, cancellationToken),
            cancellationToken);
    }

    internal static Dictionary<string, DocumentRows.Cell> Flatten(JObject document)
    {
        var row = new Dictionary<string, DocumentRows.Cell>(StringComparer.Ordinal);
        FlattenToken(document, string.Empty, row, root: true);
        return row;
    }

    private async Task<CosmosClient> OpenAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var endpoint = Required(profile, "endpoint");
        string? key = null;
        try
        {
            CosmosClient client;
            if (string.Equals(profile.GetSetting("authentication"), "ManagedIdentity", StringComparison.OrdinalIgnoreCase))
            {
                client = new CosmosClient(endpoint, new DefaultAzureCredential());
            }
            else
            {
                if (profile.Secret is not { } secret)
                {
                    throw new ConnectorException("Cosmos DB needs an account key, or authentication=ManagedIdentity.");
                }

                key = await secrets.GetAsync(profile.TenantId, secret, cancellationToken)
                    ?? throw new ConnectorException("The stored account key for this connection is missing.");
                client = new CosmosClient(endpoint, key);
            }

            return client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ConnectorException)
        {
            throw new ConnectorException(Databases.RelationalExtract.SafeMessage(ex.Message, key), ex);
        }
    }

    private static async Task<bool> ContainerExistsAsync(CosmosClient client, string database, string container, CancellationToken cancellationToken)
    {
        using var iterator = client.GetDatabase(database).GetContainerQueryIterator<ContainerProperties>();
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken))
            {
                if (string.Equals(item.Id, container, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, DocumentRows.Cell>> ReadAsync(
        Container container, long? maxRows, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var iterator = container.GetItemQueryIterator<JObject>(
            new QueryDefinition("SELECT * FROM c"),
            requestOptions: new QueryRequestOptions { MaxItemCount = 100 });
        long seen = 0;
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken))
            {
                if (maxRows is { } max && seen >= max)
                {
                    yield break;
                }

                seen++;
                yield return Flatten(item);
            }
        }
    }

    private static void FlattenToken(JToken token, string path, Dictionary<string, DocumentRows.Cell> into, bool root)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties())
                {
                    if (root && SystemProperties.Contains(property.Name))
                    {
                        continue;
                    }

                    var child = path.Length == 0 ? property.Name : path + "." + property.Name;
                    FlattenToken(property.Value, child, into, root: false);
                }

                break;
            case JArray array:
                into[path] = new DocumentRows.Cell(DataType.Json, array.ToString(Newtonsoft.Json.Formatting.None));
                break;
            case JValue value when value.Type is JTokenType.Null or JTokenType.Undefined:
                break;
            case JValue value:
                if (path.Length == 0)
                {
                    throw new ConnectorException("The document is not an object.");
                }

                into[path] = Classify(value);
                break;
        }
    }

    private static DocumentRows.Cell Classify(JValue value) => value.Type switch
    {
        JTokenType.Integer => new DocumentRows.Cell(DataType.Integer, Convert.ToInt64(value.Value, CultureInfo.InvariantCulture)),
        JTokenType.Float => new DocumentRows.Cell(DataType.Decimal, Convert.ToDecimal(value.Value, CultureInfo.InvariantCulture)),
        JTokenType.Boolean => new DocumentRows.Cell(DataType.Boolean, value.Value<bool>()),
        JTokenType.Date => new DocumentRows.Cell(DataType.DateTime, value.Value<DateTime>()),
        _ => new DocumentRows.Cell(DataType.String, value.Value is null ? string.Empty : Convert.ToString(value.Value, CultureInfo.InvariantCulture)),
    };

    private static string Required(ConnectionProfile profile, string key) =>
        profile.GetSetting(key) is { Length: > 0 } value
            ? value
            : throw new ConnectorException($"Setting '{key}' is required.");
}
