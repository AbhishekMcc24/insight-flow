using System.Collections.Concurrent;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Testing;

namespace InsightFlow.Connectors.Tests;

/// <summary>Serves local fixture files as if they were uploaded files (copies, because <see cref="LocalFile"/> deletes on dispose).</summary>
public sealed class FakeSourceFileAccessor : ISourceFileAccessor
{
    private readonly ConcurrentDictionary<Guid, string> _files = new();

    public Guid Add(string path)
    {
        var id = Guid.NewGuid();
        _files[id] = path;
        return id;
    }

    public Task<bool> ExistsAsync(TenantId tenant, Guid storedFileId, CancellationToken cancellationToken) =>
        Task.FromResult(_files.ContainsKey(storedFileId));

    public Task<LocalFile> DownloadAsync(TenantId tenant, Guid storedFileId, string extension, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_files.TryGetValue(storedFileId, out var source))
        {
            throw new ConnectorException("The uploaded file no longer exists.");
        }

        var copy = Path.Combine(Path.GetTempPath(), $"insightflow-fake-{Guid.NewGuid():N}{extension}");
        File.Copy(source, copy);
        return Task.FromResult(new LocalFile(copy));
    }
}

/// <summary>Secret store kept in memory, honouring the tenant check like the real stores.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new();

    public Task<SecretReference> SaveAsync(TenantId tenant, string value, CancellationToken cancellationToken)
    {
        var reference = SecretReference.New(tenant);
        _secrets[reference.Name] = value;
        return Task.FromResult(reference);
    }

    public Task<string?> GetAsync(TenantId tenant, SecretReference reference, CancellationToken cancellationToken) =>
        reference.TenantId != tenant
            ? throw new InvalidOperationException("Cross-tenant secret access.")
            : Task.FromResult(_secrets.GetValueOrDefault(reference.Name));

    public Task DeleteAsync(TenantId tenant, SecretReference reference, CancellationToken cancellationToken)
    {
        _secrets.TryRemove(reference.Name, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Records uploads instead of talking to Blob Storage.</summary>
public sealed class RecordingExtractUploader : IExtractUploader
{
    public List<(TenantId Tenant, Guid VersionId, long Bytes)> Uploads { get; } = [];

    public Task UploadAsync(TenantId tenant, Guid datasetVersionId, string localParquetPath, CancellationToken cancellationToken)
    {
        Uploads.Add((tenant, datasetVersionId, new FileInfo(localParquetPath).Length));
        return Task.CompletedTask;
    }
}

/// <summary>Retail fixture files shared by the file connector tests (generated once per test run).</summary>
public static class RetailFiles
{
    public const int Rows = 1_000;

    private static readonly Lazy<Task<(string Csv, string Parquet)>> Files = new(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "insightflow-connector-fixtures", Guid.NewGuid().ToString("N"));
        var csv = Path.Combine(dir, "retail_sales.csv");
        var parquet = Path.Combine(dir, "retail_sales.parquet");
        await RetailDataGenerator.WriteDenormalizedAsync(csv, Rows, csv: true);
        await RetailDataGenerator.WriteDenormalizedAsync(parquet, Rows, csv: false);
        return (csv, parquet);
    });

    public static Task<(string Csv, string Parquet)> GetAsync() => Files.Value;
}
