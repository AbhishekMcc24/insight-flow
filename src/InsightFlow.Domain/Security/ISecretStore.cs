using System.Diagnostics.CodeAnalysis;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Security;

/// <summary>
/// Opaque pointer to a stored secret (e.g. a database password). Connection profiles and other rows store only
/// this reference — never the credential. The name embeds the owning tenant so a reference from one tenant can
/// never be resolved by another, and it only uses characters Azure Key Vault accepts in secret names.
/// </summary>
public readonly record struct SecretReference
{
    private SecretReference(string name, TenantId tenant)
    {
        Name = name;
        TenantId = tenant;
    }

    /// <summary>Secret name, e.g. <c>t-&lt;tenant-guid-n&gt;-&lt;secret-guid-n&gt;</c> (≤ 127 chars, alphanumerics and dashes).</summary>
    public string Name { get; }

    public TenantId TenantId { get; }

    public static SecretReference New(TenantId tenant)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A secret needs a tenant.");
        }

        return new SecretReference($"t-{tenant.Value:N}-{Guid.CreateVersion7():N}", tenant);
    }

    public static bool TryParse([NotNullWhen(true)] string? name, out SecretReference reference)
    {
        reference = default;
        if (name is not { Length: 67 } || !name.StartsWith("t-", StringComparison.Ordinal) || name[34] != '-')
        {
            return false;
        }

        if (!Guid.TryParseExact(name.AsSpan(2, 32), "N", out var tenant) || !Guid.TryParseExact(name.AsSpan(35, 32), "N", out _))
        {
            return false;
        }

        reference = new SecretReference(name, new TenantId(tenant));
        return true;
    }

    public override string ToString() => Name;
}

/// <summary>
/// Stores credentials outside the metadata database: Azure Key Vault in the cloud, a git-ignored local file in
/// development. Implementations must refuse to read a reference that belongs to a different tenant.
/// </summary>
public interface ISecretStore
{
    Task<SecretReference> SaveAsync(TenantId tenant, string value, CancellationToken cancellationToken);

    /// <summary>Returns the secret value, or null when it does not exist. Throws when <paramref name="reference"/> belongs to another tenant.</summary>
    Task<string?> GetAsync(TenantId tenant, SecretReference reference, CancellationToken cancellationToken);

    Task DeleteAsync(TenantId tenant, SecretReference reference, CancellationToken cancellationToken);
}
