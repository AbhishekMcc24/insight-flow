using InsightFlow.Connectors.Databases;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Connectors.Files;
using InsightFlow.Connectors.Stubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InsightFlow.Connectors;

/// <summary>
/// Registers every connector, the registry and the extract pipeline. Requires a <c>BlobServiceClient</c> and an
/// <c>ISecretStore</c>. To add a connector: implement <see cref="IDataSourceConnector"/> and add one line here.
/// </summary>
public static class ConnectorsServiceCollectionExtensions
{
    public static IServiceCollection AddInsightFlowConnectors(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ConnectorOptions>()
            .Bind(configuration.GetSection(ConnectorOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISourceFileAccessor, BlobSourceFileAccessor>();
        services.TryAddSingleton<IExtractUploader, BlobExtractUploader>();

        // Reference implementations.
        services.AddSingleton<IDataSourceConnector, CsvConnector>();
        services.AddSingleton<IDataSourceConnector, ParquetConnector>();
        services.AddSingleton<IDataSourceConnector, SqlServerConnector>();

        // Registered stubs (TODO(dev2)).
        services.AddSingleton<IDataSourceConnector, ExcelConnector>();
        services.AddSingleton<IDataSourceConnector, PostgreSqlConnector>();
        services.AddSingleton<IDataSourceConnector, MySqlDbConnector>();
        services.AddSingleton<IDataSourceConnector, OracleConnector>();
        services.AddSingleton<IDataSourceConnector, MongoDbConnector>();
        services.AddSingleton<IDataSourceConnector, CosmosDbConnector>();

        services.TryAddSingleton<IConnectorRegistry, ConnectorRegistry>();
        services.TryAddSingleton<ExtractPipeline>();
        return services;
    }
}
