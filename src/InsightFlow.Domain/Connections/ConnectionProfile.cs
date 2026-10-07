using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Connections;

/// <summary>Every source type Insight Flow can ingest in v1 (D13).</summary>
public enum DataSourceKind
{
    Csv,
    Excel,
    Parquet,
    SqlServer,
    PostgreSql,
    MySql,
    Oracle,
    MongoDb,
    CosmosDb,
}

/// <summary>
/// A saved connection to a source system. <see cref="Settings"/> holds only non-secret values (server, database,
/// user name…); the credential lives in the secret store and the profile keeps just its <see cref="SecretReference"/>.
/// File-based sources use a transient profile whose <c>storedFileId</c> setting points at an uploaded file.
/// </summary>
public sealed class ConnectionProfile : ITenantOwned
{
    public const int MaxNameLength = 200;
    public const string StoredFileIdSetting = "storedFileId";

    private ConnectionProfile()
    {
        Name = string.Empty;
        Settings = new Dictionary<string, string>();
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public string Name { get; private set; }

    public DataSourceKind Kind { get; private init; }

    public IReadOnlyDictionary<string, string> Settings { get; private set; }

    public SecretReference? Secret { get; private set; }

    public string CreatedBy { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static ConnectionProfile Create(
        TenantId tenant, string name, DataSourceKind kind, IReadOnlyDictionary<string, string> settings, SecretReference? secret, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A connection needs a tenant.");
        }

        if (secret is { } s && s.TenantId != tenant)
        {
            throw new DomainRuleException("cross_tenant_secret", "The secret belongs to another tenant.");
        }

        return new ConnectionProfile
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            Name = NormalizeName(name),
            Kind = kind,
            Settings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase),
            Secret = secret,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
        };
    }

    /// <summary>A profile that is never stored: it lets file connectors read an uploaded file through the same interface.</summary>
    public static ConnectionProfile ForStoredFile(TenantId tenant, DataSourceKind kind, Guid storedFileId, string name) =>
        Create(tenant, name, kind, new Dictionary<string, string> { [StoredFileIdSetting] = storedFileId.ToString("D") }, null, "system", DateTimeOffset.UnixEpoch);

    public string? GetSetting(string key) => Settings.TryGetValue(key, out var value) ? value : null;

    private static string NormalizeName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > MaxNameLength
            ? throw new DomainRuleException("invalid_connection_name", $"A connection name must be 1–{MaxNameLength} characters.")
            : trimmed;
    }
}
