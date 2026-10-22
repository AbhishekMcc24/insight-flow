namespace InsightFlow.Domain.Threads;

/// <summary>
/// Durable files for one deployment: uploaded sources and Parquet extracts under a single directory.
/// Every service must use the same root. Set <c>FileStorage:Root</c> (or connection string <c>storage</c>)
/// to a directory on the server; when neither is set, <see cref="DefaultRoot"/> is used.
/// </summary>
public static class LocalStorage
{
    public const string RootConfigurationKey = "FileStorage:Root";

    public const string ConnectionStringName = "storage";

    /// <summary>Per-user directory used when no root is configured (local development).</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InsightFlow",
        "storage");

    /// <summary><paramref name="configured"/> wins, then <paramref name="connectionString"/>, then <see cref="DefaultRoot"/>.</summary>
    public static string ChooseRoot(string? configured, string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return Path.GetFullPath(connectionString);
        }

        return DefaultRoot;
    }

    /// <summary>Absolute path of a <see cref="StoragePaths"/> relative path. Rejects anything that escapes <paramref name="root"/>.</summary>
    public static string Resolve(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Storage paths are relative.", nameof(relativePath));
        }

        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!combined.StartsWith(prefix, comparison) && !combined.Equals(rootFull, comparison))
        {
            throw new ArgumentException("Storage path escapes the root directory.", nameof(relativePath));
        }

        return combined;
    }

    /// <summary>Creates the file from <paramref name="content"/>. Fails if it already exists.</summary>
    public static async Task WriteNewAsync(string root, string relativePath, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var destination = Prepare(root, relativePath);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, useAsync: true);
        await content.CopyToAsync(output, cancellationToken);
    }

    /// <summary>Copies <paramref name="sourceFile"/> into storage. Fails if the destination already exists.</summary>
    public static async Task CopyNewAsync(string root, string relativePath, string sourceFile, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        await using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true);
        await WriteNewAsync(root, relativePath, input, cancellationToken);
    }

    public static bool Exists(string root, string relativePath) => File.Exists(Resolve(root, relativePath));

    private static string Prepare(string root, string relativePath)
    {
        var destination = Resolve(root, relativePath);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return destination;
    }
}
