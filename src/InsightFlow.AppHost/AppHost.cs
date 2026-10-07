using InsightFlow.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Cloud-only resources. Key Vault has no local emulator, so it is added only when publishing to
// Azure; `aspire run` never provisions anything in a real subscription (see docs/adr/0019).
// ---------------------------------------------------------------------------------------------
var isPublish = builder.ExecutionContext.IsPublishMode;
if (isPublish)
{
    builder.AddAzureContainerAppEnvironment("aca");
}

var keyVault = isPublish ? builder.AddAzureKeyVault("keyvault") : null;

// ---------------------------------------------------------------------------------------------
// Backing services: Azure resources when published, containers/emulators locally.
// ---------------------------------------------------------------------------------------------
var postgres = builder.AddAzurePostgresFlexibleServer("postgres")
    .RunAsContainer(pg => pg
        .WithDataVolume("insightflow-postgres-data")
        .WithLifetime(ContainerLifetime.Persistent));
var database = postgres.AddDatabase(ResourceNames.Database);

var redis = builder.AddAzureManagedRedis(ResourceNames.Redis)
    .RunAsContainer(r => r.WithLifetime(ContainerLifetime.Persistent));

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(e => e
        .WithDataVolume("insightflow-azurite-data")
        .WithLifetime(ContainerLifetime.Persistent));
var blobs = storage.AddBlobs(ResourceNames.Blobs);
storage.AddBlobContainer(ResourceNames.ExtractsContainer);
storage.AddBlobContainer(ResourceNames.FilesContainer);

// ---------------------------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------------------------
var migrations = builder.AddProject<Projects.InsightFlow_MigrationService>(ResourceNames.Migrations)
    .WithReference(database)
    .WaitFor(database);

var queryService = builder.AddProject<Projects.InsightFlow_QueryService>(ResourceNames.QueryService)
    .WithReference(database)
    .WithReference(redis)
    .WithReference(blobs)
    .WaitFor(redis)
    .WaitFor(storage)
    .WaitForCompletion(migrations)
    .WithKeyVault(keyVault);

var agentService = builder.AddProject<Projects.InsightFlow_AgentService>(ResourceNames.AgentService)
    .WithReference(database)
    .WithReference(redis)
    .WithReference(blobs)
    .WithReference(queryService)
    // AI provider settings: AppHost user-secrets locally (optional; unset providers are disabled),
    // secure deployment parameters in Azure. See README "AI provider keys".
    .WithOptionalParameter("Ai__AzureOpenAI__Endpoint", "azure-openai-endpoint", secret: false)
    .WithOptionalParameter("Ai__AzureOpenAI__ApiKey", "azure-openai-api-key", secret: true)
    .WithOptionalParameter("Ai__Anthropic__ApiKey", "anthropic-api-key", secret: true)
    .WaitFor(redis)
    .WaitFor(storage)
    .WaitForCompletion(migrations)
    .WithKeyVault(keyVault);

var api = builder.AddProject<Projects.InsightFlow_Api>(ResourceNames.Api)
    .WithReference(database)
    .WithReference(redis)
    .WithReference(blobs)
    .WithReference(queryService)
    .WaitFor(redis)
    .WaitFor(storage)
    .WaitForCompletion(migrations)
    .WithKeyVault(keyVault);

builder.AddProject<Projects.InsightFlow_Worker>(ResourceNames.Worker)
    .WithReference(database)
    .WithReference(blobs)
    .WaitFor(storage)
    .WaitForCompletion(migrations)
    .WithKeyVault(keyVault);

builder.AddProject<Projects.InsightFlow_Web>(ResourceNames.Web)
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WithReference(queryService)
    .WithReference(agentService)
    .WaitFor(api)
    .WaitFor(queryService)
    .WaitFor(agentService);

builder.Build().Run();
