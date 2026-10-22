using System.ComponentModel.DataAnnotations;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Api.Workspace;

/// <summary>Limits and storage for workspace uploads (section <c>Uploads</c>). Validated at startup.</summary>
public sealed class UploadOptions
{
    public const string SectionName = "Uploads";

    /// <summary>Directory shared with the Worker and QueryService (<c>FileStorage:Root</c>). Filled in at startup.</summary>
    [Required]
    public string StorageRoot { get; set; } = LocalStorage.DefaultRoot;

    /// <summary>Maximum size of one file (default 2 GiB).</summary>
    [Range(1, long.MaxValue)]
    public long MaxFileBytes { get; set; } = 2L << 30;

    /// <summary>Maximum size of one multipart request (default 10 GiB).</summary>
    [Range(1, long.MaxValue)]
    public long MaxRequestBytes { get; set; } = 10L << 30;

    /// <summary>Maximum files per request.</summary>
    [Range(1, 100_000)]
    public int MaxFilesPerRequest { get; set; } = 1_000;

    /// <summary>Lower-case extensions (with dot) that may be uploaded. Executables and scripts are deliberately absent.</summary>
    [MinLength(1)]
    public IList<string> AllowedExtensions { get; } =
    [
        ".csv", ".tsv", ".txt", ".parquet", ".xlsx", ".xls", ".json", ".xml",
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".md", ".docx", ".pptx",
    ];
}

/// <summary>
/// Hook for malware scanning of uploaded files (e.g. Microsoft Defender for Storage or an ICAP service).
/// TODO(dev2): replace <see cref="NoOpUploadScanner"/> with a real scanner before accepting untrusted public uploads.
/// </summary>
public interface IUploadScanner
{
    /// <summary>Returns null when clean, or a user-safe reason to reject the file.</summary>
    Task<string?> ScanAsync(Guid storedFileId, string sha256, CancellationToken cancellationToken);
}

internal sealed class NoOpUploadScanner : IUploadScanner
{
    public Task<string?> ScanAsync(Guid storedFileId, string sha256, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
