namespace InsightFlow.AppHost;

/// <summary>
/// Resource names used in the AppHost. They double as connection-string names
/// (<c>ConnectionStrings:insightflow</c>) and service-discovery hosts (<c>https+http://queryservice</c>),
/// so services and integration tests must use exactly these values.
/// </summary>
internal static class ResourceNames
{
    public const string Database = "insightflow";
    public const string Redis = "redis";
    public const string Blobs = "blobs";
    public const string ExtractsContainer = "extracts";
    public const string FilesContainer = "files";
    public const string Migrations = "migrations";
    public const string QueryService = "queryservice";
    public const string AgentService = "agentservice";
    public const string Api = "api";
    public const string Worker = "worker";
    public const string Web = "web";
}
