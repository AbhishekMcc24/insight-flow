namespace InsightFlow.Tests.Architecture;

/// <summary>
/// Keeps heavyweight or sensitive dependencies in the one project that owns them
/// (e.g. AI SDKs only in Agent, database drivers only in Infrastructure, UI assets only in Web).
/// </summary>
public sealed class PackagePlacementTests
{
    public static readonly TheoryData<string, string[]> PackageOwners = new()
    {
        // Package id prefix -> projects allowed to reference it directly.
        { "DuckDB.NET", ["InsightFlow.Infrastructure", "InsightFlow.Agent"] }, // Agents: the AI-SQL sandbox (ADR 0022)
        { "Microsoft.EntityFrameworkCore", ["InsightFlow.Repository"] },
        { "Npgsql.EntityFrameworkCore", ["InsightFlow.Repository"] },
        { "Aspire.Npgsql.EntityFrameworkCore", ["InsightFlow.QueryService", "InsightFlow.AgentService", "InsightFlow.Api", "InsightFlow.Worker", "InsightFlow.MigrationService"] },
        { "Microsoft.Agents.AI", ["InsightFlow.Agent"] },
        { "Microsoft.Extensions.AI", ["InsightFlow.Agent"] },
        { "OpenAI", ["InsightFlow.Agent"] },
        { "Anthropic", ["InsightFlow.Agent"] },
        { "Microsoft.Data.SqlClient", ["InsightFlow.Infrastructure"] },
        { "MySqlConnector", ["InsightFlow.Infrastructure"] },
        { "Oracle.ManagedDataAccess", ["InsightFlow.Infrastructure"] },
        { "MongoDB.Driver", ["InsightFlow.Infrastructure"] },
        { "Microsoft.Azure.Cosmos", ["InsightFlow.Infrastructure"] },
        { "ExcelDataReader", ["InsightFlow.Infrastructure"] },
        { "PdfPig", ["InsightFlow.Infrastructure"] },
        { "MCC.Foundation.Neo4JHelper", ["InsightFlow.Infrastructure"] },
        { "MCC.Foundation.Guardrails", ["InsightFlow.Agent"] }, // the front guardrail layer: vulgarity and prompt injection
        { "Neo4j.Driver", ["InsightFlow.Infrastructure"] },
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
