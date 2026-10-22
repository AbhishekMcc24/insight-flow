using InsightFlow.Domain.Threads;
using InsightFlow.Connectors.Databases;
using InsightFlow.Connectors.Documents;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Connectors.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InsightFlow.Connectors;

/// <summary>
/// Registers every connector, the registry and the extract pipeline. Requires <c>FileStorage:Root</c>
/// (or connection string <c>storage</c>) and an <c>ISecretStore</c>. To add a connector: implement
/// <see cref="IDataSourceConnector"/> and add one line here.
/// </summary>
public static class ConnectorsServiceCollectionExtensions
{
    public static IServiceCollection AddInsightFlowConnectors(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ConnectorOptions>()
            .Bind(configuration.GetSection(ConnectorOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart()
            .PostConfigure(o => o.StorageRoot = LocalStorage.ChooseRoot(
                configuration[LocalStorage.RootConfigurationKey],
                configuration.GetConnectionString(LocalStorage.ConnectionStringName)));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISourceFileAccessor, DirectorySourceFileAccessor>();
        services.TryAddSingleton<IExtractUploader, DirectoryExtractUploader>();

        services.AddSingleton<IDataSourceConnector, CsvConnector>();
        services.AddSingleton<IDataSourceConnector, ParquetConnector>();
        services.AddSingleton<IDataSourceConnector, ExcelConnector>();
        services.AddSingleton<IDataSourceConnector, SqlServerConnector>();
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
