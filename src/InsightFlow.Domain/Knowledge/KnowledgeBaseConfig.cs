using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Knowledge;

/// <summary>
/// A tenant's connection to its Neo4j knowledge graph (one per tenant). The password is never stored here, only a
/// <see cref="SecretReference"/> into the secret store. Also remembers the last connection test and the last seeding,
/// so the configuration screen can show status without opening a connection.
/// </summary>
public sealed class KnowledgeBaseConfig : ITenantOwned
{
    public const int MaxUriLength = 500;
    public const int MaxUserLength = 200;
    public const int MaxDatabaseLength = 100;
    public const int MaxMessageLength = 500;

    private static readonly string[] AllowedSchemes = ["bolt", "bolt+s", "bolt+ssc", "neo4j", "neo4j+s", "neo4j+ssc"];

    private KnowledgeBaseConfig()
    {
        Uri = string.Empty;
        User = string.Empty;
        Database = "neo4j";
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public string Uri { get; private set; }

    public string User { get; private set; }

    public string Database { get; private set; }

    /// <summary>True for Neo4j Aura (always TLS); false for a self-hosted server.</summary>
    public bool UseAura { get; private set; }

    /// <summary>Encrypt the Bolt connection to a self-hosted server (Aura is always encrypted).</summary>
    public bool Encrypted { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>Name of the stored password secret; null until a password has been saved.</summary>
    public string? PasswordSecretName { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? LastTestedAt { get; private set; }

    public bool? LastTestSucceeded { get; private set; }

    public string? LastTestMessage { get; private set; }

    public DateTimeOffset? LastSeededAt { get; private set; }

    public int LastSeedTables { get; private set; }

    public int LastSeedColumns { get; private set; }

    public int LastSeedRelationships { get; private set; }

    public static KnowledgeBaseConfig Create(
        TenantId tenant, string uri, string user, string database, bool useAura, bool encrypted, bool enabled, DateTimeOffset now)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A knowledge base needs a tenant.");
        }

        var config = new KnowledgeBaseConfig { Id = Guid.CreateVersion7(), TenantId = tenant, CreatedAt = now };
        config.Update(uri, user, database, useAura, encrypted, enabled, now);
        return config;
    }

    public void Update(string uri, string user, string database, bool useAura, bool encrypted, bool enabled, DateTimeOffset now)
    {
        var trimmedUri = uri?.Trim() ?? string.Empty;
        if (!System.Uri.TryCreate(trimmedUri, UriKind.Absolute, out var parsed)
            || trimmedUri.Length > MaxUriLength
            || !AllowedSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(parsed.Host))
        {
            throw new DomainRuleException(
                "invalid_neo4j_uri",
                $"The Neo4j address must look like bolt://host:7687 or neo4j+s://host (schemes: {string.Join(", ", AllowedSchemes)}).");
        }

        var trimmedUser = user?.Trim() ?? string.Empty;
        if (trimmedUser.Length is 0 or > MaxUserLength)
        {
            throw new DomainRuleException("invalid_neo4j_user", $"The Neo4j user must be 1–{MaxUserLength} characters.");
        }

        var trimmedDatabase = string.IsNullOrWhiteSpace(database) ? "neo4j" : database.Trim();
        if (trimmedDatabase.Length > MaxDatabaseLength || !trimmedDatabase.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            throw new DomainRuleException("invalid_neo4j_database", "The database name can only contain letters, digits, '.', '-' and '_'.");
        }

        var connectionChanged = !string.Equals(Uri, trimmedUri, StringComparison.Ordinal)
            || !string.Equals(User, trimmedUser, StringComparison.Ordinal)
            || !string.Equals(Database, trimmedDatabase, StringComparison.Ordinal);

        Uri = trimmedUri;
        User = trimmedUser;
        Database = trimmedDatabase;
        UseAura = useAura;
        Encrypted = encrypted;
        Enabled = enabled;
        UpdatedAt = now;

        if (connectionChanged)
        {
            LastTestedAt = null;
            LastTestSucceeded = null;
            LastTestMessage = null;
        }
    }

    public void SetPasswordSecret(SecretReference reference, DateTimeOffset now)
    {
        PasswordSecretName = reference.Name;
        UpdatedAt = now;
        LastTestedAt = null;
        LastTestSucceeded = null;
        LastTestMessage = null;
    }

    public void RecordTest(bool succeeded, string? message, DateTimeOffset now)
    {
        LastTestedAt = now;
        LastTestSucceeded = succeeded;
        LastTestMessage = message is { Length: > MaxMessageLength } ? message[..MaxMessageLength] : message;
    }

    public void RecordSeed(int tables, int columns, int relationships, DateTimeOffset now)
    {
        LastSeededAt = now;
        LastSeedTables = tables;
        LastSeedColumns = columns;
        LastSeedRelationships = relationships;
    }
}
