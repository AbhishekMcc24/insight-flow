using Azure;
using Azure.Security.KeyVault.Secrets;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Persistence.Secrets;

/// <summary>
/// Cloud <see cref="ISecretStore"/> on Azure Key Vault. The <see cref="SecretClient"/> is registered by the Aspire
/// Key Vault client integration (managed identity in Azure Container Apps). Deletion is a soft delete; purge is left
/// to the vault's retention policy.
/// </summary>
public sealed class KeyVaultSecretStore(SecretClient client) : ISecretStore
{
    public async Task<SecretReference> SaveAsync(TenantId tenant, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var reference = SecretReference.New(tenant);
        var secret = new KeyVaultSecret(reference.Name, value);
        secret.Properties.Tags["tenant"] = tenant.ToString();
        await client.SetSecretAsync(secret, cancellationToken);
        return reference;
    }

    public async Task<string?> GetAsync(TenantId tenant, SecretReference reference, CancellationToken cancellationToken)
    {
        SecretOwnership.Ensure(tenant, reference);
        try
        {
            var response = await client.GetSecretAsync(reference.Name, cancellationToken: cancellationToken);
            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(TenantId tenant, SecretReference reference, CancellationToken cancellationToken)
    {
        SecretOwnership.Ensure(tenant, reference);
        try
        {
            await client.StartDeleteSecretAsync(reference.Name, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone.
        }
    }
}
