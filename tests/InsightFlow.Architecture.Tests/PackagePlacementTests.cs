namespace InsightFlow.Architecture.Tests;

/// <summary>
/// Keeps heavyweight or sensitive dependencies in the one project that owns them
/// (e.g. AI SDKs only in Agents, database drivers only in Connectors, UI assets only in Web).
/// </summary>
public sealed class PackagePlacementTests
{
    public static readonly TheoryData<string, string[]> PackageOwners = new()
    {
        // Package id prefix -> projects allowed to reference it directly.
        { "DuckDB.NET", ["InsightFlow.Query", "InsightFlow.Connectors", "InsightFlow.Agents"] }, // Agents: the AI-SQL sandbox (ADR 0022)
        { "Microsoft.EntityFrameworkCore", ["InsightFlow.Persistence"] },
        { "Npgsql.EntityFrameworkCore", ["InsightFlow.Persistence"] },
        { "Aspire.Npgsql.EntityFrameworkCore", ["InsightFlow.QueryService", "InsightFlow.AgentService", "InsightFlow.Api", "InsightFlow.Worker", "InsightFlow.MigrationService"] },
        { "Microsoft.Agents.AI", ["InsightFlow.Agents"] },
        { "Microsoft.Extensions.AI", ["InsightFlow.Agents"] },
        { "OpenAI", ["InsightFlow.Agents"] },
        { "Anthropic", ["InsightFlow.Agents"] },
        { "Microsoft.Data.SqlClient", ["InsightFlow.Connectors"] },
        { "MySqlConnector", ["InsightFlow.Connectors"] },
        { "Oracle.ManagedDataAccess", ["InsightFlow.Connectors"] },
        { "MongoDB.Driver", ["InsightFlow.Connectors"] },
        { "Microsoft.Azure.Cosmos", ["InsightFlow.Connectors"] },
        { "ExcelDataReader", ["InsightFlow.Connectors"] },
        { "Quartz", ["InsightFlow.Worker"] },
        { "Microsoft.AspNetCore.Components", ["InsightFlow.Web"] },
        { "Telerik", [] },
    };

    [Theory]
    [MemberData(nameof(PackageOwners))]
    public void PackageReference_OnlyInOwningProjects_Pass(string packagePrefix, string[] owners)
    {
        var offenders = RepositoryLayout.SourceProjects().Values
            .Where(p => p.PackageReferences.Any(r => IsMatch(r, packagePrefix)))
            .Select(p => p.Name)
            .Except(owners, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty($"'{packagePrefix}*' may only be referenced by: {(owners.Length == 0 ? "(no project)" : string.Join(", ", owners))}");
    }

    [Fact]
    public void FrontEndAssets_OnlyInWeb_Pass()
    {
        string[] patterns = ["*.razor", "package.json", "*.css"];
        var webDir = Path.Combine(RepositoryLayout.Src.FullName, "InsightFlow.Web");

        var offenders = patterns
            .SelectMany(p => RepositoryLayout.Src.EnumerateFiles(p, SearchOption.AllDirectories))
            .Where(f => !f.FullName.StartsWith(webDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepositoryLayout.Root.FullName, f.FullName))
            .ToList();

        offenders.ShouldBeEmpty("UI components, styles and npm manifests belong in src/InsightFlow.Web only");
    }

    private static bool IsMatch(string packageId, string prefix) =>
        packageId.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || packageId.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
}
