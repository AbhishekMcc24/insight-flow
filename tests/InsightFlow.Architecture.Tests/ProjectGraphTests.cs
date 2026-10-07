namespace InsightFlow.Architecture.Tests;

/// <summary>
/// Enforces the project dependency rules (CLAUDE.md, "Dependency rules") on the csproj files themselves,
/// so an illegal reference fails CI even before any code uses it.
/// </summary>
public sealed class ProjectGraphTests
{
    private const string Domain = "InsightFlow.Domain";
    private const string Contracts = "InsightFlow.Contracts";
    private const string Persistence = "InsightFlow.Persistence";
    private const string Query = "InsightFlow.Query";
    private const string Agents = "InsightFlow.Agents";
    private const string Connectors = "InsightFlow.Connectors";
    private const string ServiceDefaults = "InsightFlow.ServiceDefaults";

    /// <summary>The only project references each production project may have. New projects must be added here deliberately.</summary>
    public static readonly TheoryData<string, string[]> AllowedReferences = new()
    {
        { Domain, [] },
        { Contracts, [Domain] },
        { Persistence, [Domain] },
        { Query, [Domain, Contracts] },
        { Agents, [Domain, Contracts, Query] },
        { Connectors, [Domain, Contracts] },
        { ServiceDefaults, [] },
        { "InsightFlow.QueryService", [Query, Persistence, ServiceDefaults] },
        { "InsightFlow.AgentService", [Agents, Persistence, ServiceDefaults] },
        { "InsightFlow.Api", [Connectors, Persistence, ServiceDefaults] },
        { "InsightFlow.Worker", [Connectors, Persistence, ServiceDefaults] },
        { "InsightFlow.MigrationService", [Persistence, ServiceDefaults] },
        { "InsightFlow.Web", [Contracts, ServiceDefaults] },
        {
            "InsightFlow.AppHost",
            [
                "InsightFlow.MigrationService", "InsightFlow.QueryService", "InsightFlow.AgentService",
                "InsightFlow.Api", "InsightFlow.Worker", "InsightFlow.Web",
            ]
        },
    };

    [Fact]
    public void SourceProjects_AllHaveDeclaredRules()
    {
        var declared = AllowedReferences.Select(row => row.Data.Item1).ToHashSet(StringComparer.Ordinal);

        var undeclared = RepositoryLayout.SourceProjects().Keys.Where(p => !declared.Contains(p)).ToList();

        undeclared.ShouldBeEmpty("every project under src/ needs an entry in ProjectGraphTests.AllowedReferences");
    }

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void ProjectReferences_OnlyAllowedTargets_Pass(string project, string[] allowed)
    {
        var projects = RepositoryLayout.SourceProjects();
        projects.ShouldContainKey(project);

        var illegal = projects[project].ProjectReferences.Except(allowed, StringComparer.Ordinal).ToList();

        illegal.ShouldBeEmpty($"{project} may only reference: {string.Join(", ", allowed)}");
    }
}
