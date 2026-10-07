using System.Text.Json;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Persistence.Secrets;

/// <summary>
/// Development-only <see cref="ISecretStore"/>: a JSON file outside source control (default <c>.local-data/secrets.local.json</c>,
/// git-ignored). Values are stored in plain text — acceptable for local test databases only; the cloud uses Key Vault.
/// </summary>
public sealed class LocalFileSecretStore(string path) : ISecretStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task<SecretReference> SaveAsync(TenantId tenant, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var reference = SecretReference.New(tenant);
        await UpdateAsync(secrets => secrets[reference.Name] = value, cancellationToken);
        return reference;
    }

    public async Task<string?> GetAsync(TenantId tenant, SecretReference reference, CancellationToken cancellationToken)
    {
        SecretOwnership.Ensure(tenant, reference);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var secrets = await ReadAsync(cancellationToken);
            return secrets.GetValueOrDefault(reference.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DeleteAsync(TenantId tenant, SecretReference reference, CancellationToken cancellationToken)
    {
        SecretOwnership.Ensure(tenant, reference);
        return UpdateAsync(secrets => secrets.Remove(reference.Name), cancellationToken);
    }

    private async Task UpdateAsync(Action<Dictionary<string, string>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var secrets = await ReadAsync(cancellationToken);
            change(secrets);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, secrets, SecretFileJsonContext.Default.DictionaryStringString, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, SecretFileJsonContext.Default.DictionaryStringString, cancellationToken)
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SecretFileJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

internal static class SecretOwnership
{
    public static void Ensure(TenantId tenant, SecretReference reference)
    {
        if (reference.TenantId != tenant || tenant.IsEmpty)
        {
            throw new TenantIsolationException("The secret reference belongs to another tenant.");
        }
    }
}
