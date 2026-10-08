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

// Entra External ID settings become deployment parameters (prompted by `aspire deploy`); locally the
// development auth handler is used and these are not declared.
var entra = isPublish ? CloudConfiguration.EntraParameters.Add(builder) : null;

// ---------------------------------------------------------------------------------------------
// Backing services: Azure resources when published, containers/emulators locally.
// Local containers persist data across `aspire run`; integration tests set
// InsightFlow:EphemeralInfrastructure=true to get fresh, throwaway containers instead.
// ---------------------------------------------------------------------------------------------
var ephemeral = bool.TryParse(builder.Configuration["InsightFlow:EphemeralInfrastructure"], out var e) && e;

var postgres = builder.AddAzurePostgresFlexibleServer("postgres")
    .RunAsContainer(pg =>
    {
        if (!ephemeral)
        {
            pg.WithDataVolume("insightflow-postgres-data").WithLifetime(ContainerLifetime.Persistent);
        }
    });
if (keyVault is not null)
{
    // Services, EF Core and the Quartz job store connect with plain Npgsql connection strings, so Azure uses password
    // auth with the connection string kept in Key Vault. TODO(dev2): move to Entra token auth (docs/adr/0023).
    postgres.WithPasswordAuthentication(keyVault);
}

var database = postgres.AddDatabase(ResourceNames.Database);

var redis = builder.AddAzureManagedRedis(ResourceNames.Redis)
    .RunAsContainer(r =>
    {
        if (!ephemeral)
        {
            r.WithLifetime(ContainerLifetime.Persistent);
        }
    });
if (keyVault is not null)
{
    // The services use the plain StackExchange.Redis client: access-key auth, key stored in Key Vault (docs/adr/0023).
    redis.WithAccessKeyAuthentication(keyVault);
}

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(e =>
    {
        if (!ephemeral)
        {
            e.WithDataVolume("insightflow-azurite-data").WithLifetime(ContainerLifetime.Persistent);
        }
    });
var blobs = storage.AddBlobs(ResourceNames.Blobs);
storage.AddBlobContainer(ResourceNames.ExtractsContainer);
storage.AddBlobContainer(ResourceNames.FilesContainer);

// ---------------------------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------------------------
var migrations = builder.AddProject<Projects.InsightFlow_MigrationService>(ResourceNames.Migrations)
    .WithReference(database)
    .WaitFor(database)
    .WithReplicas(min: 1, max: 1);
if (isPublish)
{
    // No run-to-completion apps in Container Apps via Aspire 13.6: migrate, then idle (see MigrationWorker).
    migrations.WithEnvironment("Migrations__KeepAlive", "true");
}

var queryService = builder.AddProject<Projects.InsightFlow_QueryService>(ResourceNames.QueryService)
    .WithReference(database)
    .WithReference(redis)
    .WithReference(blobs)
    .WaitFor(redis)
    .WaitFor(storage)
    .WaitForCompletion(migrations)
    .WithKeyVault(keyVault)
    .WithEntraApi(entra)
    .WithReplicas(min: 1, max: 5);

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
    .WithKeyVault(keyVault)
    .WithEntraApi(entra)
    .WithReplicas(min: 1, max: 3);

var api = builder.AddProject<Projects.InsightFlow_Api>(ResourceNames.Api)
    .WithReference(database)
    .WithReference(redis)
    .WithReference(blobs)
    .WithReference(queryService)
    .WaitFor(redis)
    .WaitFor(storage)
    .WaitForCompletion(migrations)
    .WithKeyVault(keyVault)
    .WithEntraApi(entra)
    .WithReplicas(min: 1, max: 5);

builder.AddProject<Projects.InsightFlow_Worker>(ResourceNames.Worker)
    .WithReference(database)
    .WithReference(blobs)
    .WaitFor(storage)
    .WaitForCompletion(migrations)
    .WithKeyVault(keyVault)
    // Quartz runs clustered (one node fires each trigger); extract runs are claimed with SKIP LOCKED.
    .WithReplicas(min: 1, max: 3);

builder.AddProject<Projects.InsightFlow_Web>(ResourceNames.Web)
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WithReference(queryService)
    .WithReference(agentService)
    .WaitFor(api)
    .WaitFor(queryService)
    .WaitFor(agentService)
    .WithEntraWebApp(entra)
    // Blazor Server keeps circuits in memory: one replica until sticky sessions and a shared Data Protection key
    // ring are configured. TODO(dev2): see docs/deploy.md "Scaling the Web app".
    .WithReplicas(min: 1, max: 1);

builder.Build().Run();
