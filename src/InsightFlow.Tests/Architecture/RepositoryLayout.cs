using System.Xml.Linq;

namespace InsightFlow.Tests.Architecture;

/// <summary>Locates the repository root and parses project files so rules can be checked from source, not just binaries.</summary>
internal static class RepositoryLayout
{
    private static readonly Lazy<DirectoryInfo> LazyRoot = new(FindSolutionDirectory);

    /// <summary>The folder that holds InsightFlow.slnx (<c>src/</c>).</summary>
    public static DirectoryInfo Src => LazyRoot.Value;

    /// <summary>The repository root, one level above <c>src/</c>.</summary>
    public static DirectoryInfo Root => Src.Parent ?? Src;


    /// <summary>All production projects under <c>src/</c> (the test project excluded), keyed by project name (e.g. <c>InsightFlow.Infrastructure</c>).</summary>
    public static IReadOnlyDictionary<string, ProjectFile> SourceProjects() =>
        Src.EnumerateFiles("*.csproj", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .Select(ProjectFile.Load)
            .Where(p => p.Name != "InsightFlow.Tests")
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

    private static bool IsBuildOutput(FileInfo file) =>
        file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.FullName.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static DirectoryInfo FindSolutionDirectory()
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
