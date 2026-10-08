namespace InsightFlow.Web.Components.Workspace;

/// <summary>A dataset chosen in the Explorer or a picker.</summary>
public sealed record DatasetSelection(Guid DatasetVersionId, string Name);

/// <summary>One file in the Explorer's upload list (fed by explorerDrop.js callbacks).</summary>
public sealed class UploadRow(int id, string path, long size)
{
    public int Id { get; } = id;

    public string Path { get; } = path;

    public long Size { get; } = size;

    public long Loaded { get; set; }

    public bool Done { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    public int Percent => Done ? 100 : Size <= 0 ? 0 : (int)Math.Min(99, Loaded * 100 / Size);
}

/// <summary>What is being dragged inside the tree (drag-to-move).</summary>
public sealed record DragPayload(Guid Id, bool IsFolder, Guid ParentId);
