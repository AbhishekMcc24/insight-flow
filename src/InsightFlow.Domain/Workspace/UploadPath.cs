namespace InsightFlow.Domain.Workspace;

/// <summary>
/// The relative path of a file inside a dropped local folder (e.g. <c>Q3/eu/sales.csv</c>). Every segment is
/// validated as an <see cref="ItemName"/>, so <c>..</c>, absolute paths and drive letters are rejected and the
/// server can safely recreate the folder structure under the drop target.
/// </summary>
public sealed record UploadPath(IReadOnlyList<ItemName> Folders, ItemName FileName)
{
    public static UploadPath Parse(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new DomainRuleException("invalid_upload_path", "The upload path is empty.");
        }

        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Length > FolderTreeRules.MaxDepth)
        {
            throw new DomainRuleException("upload_path_too_deep", $"Folders can be at most {FolderTreeRules.MaxDepth} levels deep.");
        }

        var names = new List<ItemName>(segments.Length);
        foreach (var segment in segments)
        {
            if (!ItemName.TryCreate(segment, out var name, out var error))
            {
                throw new DomainRuleException("invalid_upload_path", $"Invalid path segment '{segment}': {error}");
            }

            names.Add(name);
        }

        return new UploadPath(names[..^1], names[^1]);
    }

    public bool Equals(UploadPath? other) =>
        other is not null && FileName == other.FileName && Folders.SequenceEqual(other.Folders);

    public override int GetHashCode() => HashCode.Combine(FileName, Folders.Count);

    public override string ToString() => string.Join('/', Folders.Append(FileName));
}
