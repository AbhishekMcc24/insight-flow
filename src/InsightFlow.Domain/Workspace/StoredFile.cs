using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Domain.Workspace;

/// <summary>Tabular formats that can become a dataset with one click ("Create dataset").</summary>
public enum TabularFormat
{
    Csv,
    Excel,
    Parquet,
}

/// <summary>
/// Immutable metadata of an uploaded file. The bytes live under <see cref="LocalStorage"/> at <see cref="BlobPath"/>, which is
/// built from ids only; <see cref="OriginalName"/> is kept as metadata and never used to form a path.
/// </summary>
public sealed class StoredFile : ITenantOwned
{
    private StoredFile()
    {
        OriginalName = string.Empty;
        ContentType = string.Empty;
        Sha256 = string.Empty;
        BlobPath = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public string OriginalName { get; private init; }

    public string ContentType { get; private init; }

    public long SizeBytes { get; private init; }

    /// <summary>Lower-case hex SHA-256 of the content, computed while streaming the upload.</summary>
    public string Sha256 { get; private init; }

    public string BlobPath { get; private init; }

    public string CreatedBy { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>Ids are allocated before the upload streams to Blob because the path contains the id.</summary>
    public static Guid NewId() => Guid.CreateVersion7();

    public static StoredFile Create(
        Guid id, TenantId tenant, ItemName originalName, string contentType, long sizeBytes, string sha256, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sizeBytes < 0)
        {
            throw new DomainRuleException("negative_size", "File size cannot be negative.");
        }

        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new DomainRuleException("invalid_hash", "SHA-256 must be 64 hex characters.");
        }

        return new StoredFile
        {
            Id = id,
            TenantId = tenant,
            OriginalName = originalName.Value,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256.ToLowerInvariant(),
            BlobPath = StoragePaths.File(tenant, id),
            CreatedBy = createdBy,
            CreatedAt = createdAt,
        };
    }

    /// <summary>The tabular format of a file name, if it can be turned into a dataset.</summary>
    public static TabularFormat? DetectTabularFormat(ItemName name) => name.Extension switch
    {
        ".csv" or ".tsv" or ".txt" => TabularFormat.Csv,
        ".xlsx" or ".xls" => TabularFormat.Excel,
        ".parquet" => TabularFormat.Parquet,
        _ => null,
    };
}
