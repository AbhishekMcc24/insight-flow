namespace InsightFlow.Domain.Tenancy;

/// <summary>
/// A customer organisation. The registry of tenants; every other tenant-owned row points here through its
/// <see cref="TenantId"/>. Membership and roles come from the identity provider's token claims in v1
/// (role-management screens are TODO(dev2)).
/// </summary>
public sealed class Tenant
{
    public const int MaxNameLength = 200;

    private Tenant()
    {
        Name = string.Empty;
    }

    public TenantId Id { get; private init; }

    public string Name { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static Tenant Create(TenantId id, string name, DateTimeOffset createdAt)
    {
        if (id.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A tenant needs an id.");
        }

        return new Tenant { Id = id, Name = NormalizeName(name), CreatedAt = createdAt };
    }

    public void Rename(string name) => Name = NormalizeName(name);

    private static string NormalizeName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > MaxNameLength
            ? throw new DomainRuleException("invalid_tenant_name", $"A tenant name must be 1–{MaxNameLength} characters.")
            : trimmed;
    }
}
