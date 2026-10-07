using System.Xml.Linq;

namespace InsightFlow.Architecture.Tests;

/// <summary>Locates the repository root and parses project files so rules can be checked from source, not just binaries.</summary>
internal static class RepositoryLayout
{
    private static readonly Lazy<DirectoryInfo> LazyRoot = new(FindRoot);

    public static DirectoryInfo Root => LazyRoot.Value;

    public static DirectoryInfo Src => new(Path.Combine(Root.FullName, "src"));

    /// <summary>All production projects under <c>src/</c>, keyed by project name (e.g. <c>InsightFlow.Query</c>).</summary>
    public static IReadOnlyDictionary<string, ProjectFile> SourceProjects() =>
        Src.EnumerateFiles("*.csproj", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .Select(ProjectFile.Load)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

    private static bool IsBuildOutput(FileInfo file) =>
        file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.FullName.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static DirectoryInfo FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InsightFlow.slnx")))
        {
            dir = dir.Parent;
        }

        return dir ?? throw new InvalidOperationException("Could not locate InsightFlow.slnx above the test output directory.");
    }
}

/// <summary>A parsed <c>.csproj</c>: its project references and package references.</summary>
internal sealed record ProjectFile(
    string Name,
    string Path,
    string Sdk,
    IReadOnlySet<string> ProjectReferences,
    IReadOnlySet<string> PackageReferences)
{
    public static ProjectFile Load(FileInfo file)
    {
        var doc = XDocument.Load(file.FullName);
        var root = doc.Root ?? throw new InvalidOperationException($"Empty project file {file.FullName}");

        var projectRefs = root.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Select(include => System.IO.Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToHashSet(StringComparer.Ordinal);

        var packageRefs = root.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ProjectFile(
            System.IO.Path.GetFileNameWithoutExtension(file.Name),
            file.FullName,
            (string?)root.Attribute("Sdk") ?? string.Empty,
            projectRefs,
            packageRefs);
    }
}
