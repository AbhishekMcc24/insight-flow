using System.Globalization;
using System.Runtime.CompilerServices;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Security;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace InsightFlow.Connectors.Documents;

/// <summary>
/// MongoDB. Settings: <c>host</c>, <c>database</c>, optional <c>port</c> (default 27017), <c>user</c>
/// (password is the profile secret) and <c>authSource</c> (default <c>admin</c>). A profile with no user connects
/// without credentials. Discovery lists collections. Schema comes from the first
/// <see cref="ConnectorOptions.DocumentSampleSize"/> documents.
/// </summary>
public sealed class MongoDbConnector(ISecretStore secrets, IOptions<ConnectorOptions> options) : IDataSourceConnector
{
    public DataSourceKind Kind => DataSourceKind.MongoDb;

    public ConnectorCapabilities Capabilities { get; } = new(SupportsLivePushdown: false, IsDocumentStore: true, SupportsIncremental: false);

    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var (client, database) = await ConnectAsync(profile, cancellationToken);
            using var _ = client;
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return ConnectionTestResult.Ok($"Connected to {database.DatabaseNamespace.DatabaseName}.");
        }
        catch (ConnectorException ex)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
    }

    public async IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (client, database) = await ConnectAsync(profile, cancellationToken);
        using var _ = client;
        using var cursor = await database.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var name in cursor.Current)
            {
                if (name.StartsWith("system.", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return new SourceTable(name, name, Kind: "collection");
            }
        }
    }

    public async Task<ExtractResult> ExtractAsync(ExtractRequest request, IExtractWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        if (string.IsNullOrWhiteSpace(request.Table))
        {
            throw new ConnectorException("A collection is required.");
        }

        var (client, database) = await ConnectAsync(request.Profile, cancellationToken);
        using var _ = client;
        if (!await CollectionExistsAsync(database, request.Table, cancellationToken))
        {
            throw new ConnectorException($"Table '{request.Table}' was not found.");
        }

        var collection = database.GetCollection<BsonDocument>(request.Table);
        return await DocumentRows.WriteAsync(
            writer,
            Math.Max(1, options.Value.DocumentSampleSize),
            ReadAsync(collection, request.MaxRows, cancellationToken),
            cancellationToken);
    }

    /// <summary>Builds the MongoDB URL from settings + secret. Never logged; exposed for tests.</summary>
    internal async Task<string> BuildConnectionStringAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var host = profile.GetSetting("host");
        var database = profile.GetSetting("database");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database))
        {
            throw new ConnectorException("Settings 'host' and 'database' are required.");
        }

        var builder = new MongoUrlBuilder
        {
            Server = new MongoServerAddress(host, RelationalExtractPort(profile)),
            DatabaseName = database,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ServerSelectionTimeout = TimeSpan.FromSeconds(15),
            ApplicationName = "InsightFlow",
        };

        var user = profile.GetSetting("user");
        if (!string.IsNullOrWhiteSpace(user))
        {
            if (profile.Secret is not { } secret)
            {
                throw new ConnectorException("MongoDB authentication needs a password.");
            }

            builder.Username = user;
            builder.Password = await secrets.GetAsync(profile.TenantId, secret, cancellationToken)
                ?? throw new ConnectorException("The stored password for this connection is missing.");
            builder.AuthenticationSource = string.IsNullOrWhiteSpace(profile.GetSetting("authSource"))
                ? "admin"
                : profile.GetSetting("authSource");
        }

        return builder.ToMongoUrl().ToString();
    }

    internal static Dictionary<string, DocumentRows.Cell> Flatten(BsonDocument document)
    {
        var row = new Dictionary<string, DocumentRows.Cell>(StringComparer.Ordinal);
        FlattenValue(document, string.Empty, row);
        return row;
    }

    private async Task<(MongoClient Client, IMongoDatabase Database)> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        string? password = null;
        if (profile.Secret is { } secret)
        {
            password = await secrets.GetAsync(profile.TenantId, secret, cancellationToken);
        }

        try
        {
            var url = new MongoUrl(await BuildConnectionStringAsync(profile, cancellationToken));
            var client = new MongoClient(url);
            return (client, client.GetDatabase(url.DatabaseName));
        }
        catch (MongoException ex)
        {
            throw new ConnectorException(Databases.RelationalExtract.SafeMessage(ex.Message, password), ex);
        }
    }

    private static async Task<bool> CollectionExistsAsync(IMongoDatabase database, string name, CancellationToken cancellationToken)
    {
        using var cursor = await database.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            if (cursor.Current.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, DocumentRows.Cell>> ReadAsync(
        IMongoCollection<BsonDocument> collection, long? maxRows, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var find = new FindOptions<BsonDocument> { BatchSize = 256 };
        if (maxRows is { } max)
        {
            find.Limit = max > int.MaxValue ? int.MaxValue : (int)max;
        }

        using var cursor = await collection.FindAsync(FilterDefinition<BsonDocument>.Empty, find, cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var document in cursor.Current)
            {
                yield return Flatten(document);
            }
        }
    }

    private static void FlattenValue(BsonValue value, string path, Dictionary<string, DocumentRows.Cell> into)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                foreach (var element in value.AsBsonDocument)
                {
                    var child = path.Length == 0 ? element.Name : path + "." + element.Name;
                    FlattenValue(element.Value, child, into);
                }

                break;
            case BsonType.Array:
                into[path] = new DocumentRows.Cell(DataType.Json, ToJson(value));
                break;
            case BsonType.Null:
            case BsonType.Undefined:
                break;
            default:
                if (path.Length == 0)
                {
                    throw new ConnectorException("The document is not an object.");
                }

                if (Classify(value) is { } cell)
                {
                    into[path] = cell;
                }

                break;
        }
    }

    private static DocumentRows.Cell? Classify(BsonValue value) => value.BsonType switch
    {
        BsonType.Int32 => new DocumentRows.Cell(DataType.Integer, (long)value.AsInt32),
        BsonType.Int64 => new DocumentRows.Cell(DataType.Integer, value.AsInt64),
        BsonType.Double => new DocumentRows.Cell(DataType.Decimal, Convert.ToDecimal(value.AsDouble, CultureInfo.InvariantCulture)),
        BsonType.Decimal128 => new DocumentRows.Cell(DataType.Decimal, Decimal128.ToDecimal(value.AsDecimal128)),
        BsonType.Boolean => new DocumentRows.Cell(DataType.Boolean, value.AsBoolean),
        BsonType.DateTime => new DocumentRows.Cell(DataType.DateTime, value.ToUniversalTime()),
        BsonType.String => new DocumentRows.Cell(DataType.String, value.AsString),
        BsonType.ObjectId => new DocumentRows.Cell(DataType.String, value.AsObjectId.ToString()),
        BsonType.Symbol => new DocumentRows.Cell(DataType.String, value.AsString),
        _ => new DocumentRows.Cell(DataType.String, value.ToString()),
    };

    private static string ToJson(BsonValue value) => value.BsonType switch
    {
        BsonType.Array => "[" + string.Join(',', value.AsBsonArray.Select(ToJson)) + "]",
        BsonType.Document => "{" + string.Join(',', value.AsBsonDocument.Select(e => DocumentRows.JsonString(e.Name) + ":" + ToJson(e.Value))) + "}",
        BsonType.String or BsonType.Symbol or BsonType.ObjectId => DocumentRows.JsonString(value.ToString()!),
        BsonType.Boolean => value.AsBoolean ? "true" : "false",
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 => DocumentRows.Format((IFormattable)BsonTypeMapper.MapToDotNetValue(value)),
        BsonType.Null or BsonType.Undefined => "null",
        BsonType.DateTime => DocumentRows.JsonString(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        _ => DocumentRows.JsonString(value.ToString() ?? string.Empty),
    };

    private static int RelationalExtractPort(ConnectionProfile profile) =>
        Databases.RelationalExtract.ParsePort(profile.GetSetting("port"), 27017);
}
