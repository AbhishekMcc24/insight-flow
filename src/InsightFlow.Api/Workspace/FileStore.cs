using System.Security.Cryptography;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using Microsoft.Extensions.Options;

namespace InsightFlow.Api.Workspace;

/// <summary>Raw bytes of uploaded files under the shared storage directory. Paths are built from ids only.</summary>
public interface IFileStore
{
    /// <summary>Streams <paramref name="content"/> to <c>tenants/{tenant}/files/{id}</c>; never overwrites.</summary>
    Task UploadAsync(TenantId tenant, Guid storedFileId, Stream content, string contentType, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(TenantId tenant, Guid storedFileId, CancellationToken cancellationToken);

    Task DeleteAsync(TenantId tenant, Guid storedFileId, CancellationToken cancellationToken);
}

internal sealed class DirectoryFileStore(IOptions<UploadOptions> options) : IFileStore
{
    public Task UploadAsync(TenantId tenant, Guid storedFileId, Stream content, string contentType, CancellationToken cancellationToken)
    {
        _ = contentType;
        return LocalStorage.WriteNewAsync(options.Value.StorageRoot, StoragePaths.File(tenant, storedFileId), content, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(TenantId tenant, Guid storedFileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = LocalStorage.Resolve(options.Value.StorageRoot, StoragePaths.File(tenant, storedFileId));
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(TenantId tenant, Guid storedFileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = LocalStorage.Resolve(options.Value.StorageRoot, StoragePaths.File(tenant, storedFileId));
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Thrown when an upload exceeds the per-file size limit.</summary>
public sealed class UploadTooLargeException : Exception
{
    public UploadTooLargeException()
    {
    }

    public UploadTooLargeException(string message)
        : base(message)
    {
    }

    public UploadTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Read-through stream that counts bytes, computes SHA-256 incrementally and enforces a size limit, so an upload is
/// hashed and bounded while it streams to the storage directory — nothing is buffered in memory.
/// </summary>
internal sealed class HashingStream(Stream inner, long maxBytes) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public string GetHashHex() => Convert.ToHexStringLower(_hash.GetCurrentHash());

    public override int Read(byte[] buffer, int offset, int count) => Track(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        return Track(buffer.Span[..read]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Track(ReadOnlySpan<byte> data)
    {
        BytesRead += data.Length;
        if (BytesRead > maxBytes)
        {
            throw new UploadTooLargeException($"The file exceeds the {maxBytes / (1024 * 1024):N0} MB limit.");
        }

        _hash.AppendData(data);
        return data.Length;
    }
}
