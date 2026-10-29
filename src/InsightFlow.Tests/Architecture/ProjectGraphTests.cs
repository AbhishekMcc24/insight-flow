namespace InsightFlow.Tests.Architecture;

/// <summary>
/// Enforces the project dependency rules (CLAUDE.md, "Dependency rules") on the csproj files themselves,
/// so an illegal reference fails CI even before any code uses it.
/// </summary>
public sealed class ProjectGraphTests
{
    private const string DomainProject = "InsightFlow.Domain";
    private const string Foundation = "InsightFlow.Foundation";
    private const string Infrastructure = "InsightFlow.Infrastructure";
    private const string Repository = "InsightFlow.Repository";
    private const string Agent = "InsightFlow.Agent";
    private const string Logic = "InsightFlow.Logic";

    /// <summary>The only project references each production project may have. New projects must be added here deliberately.</summary>
    public static readonly TheoryData<string, string[]> AllowedReferences = new()
    {
        { DomainProject, [] },
        { Foundation, [] },
        { Repository, [DomainProject] },
        { Infrastructure, [DomainProject] },
        { Agent, [DomainProject, Infrastructure] },
        { Logic, [DomainProject, Foundation, Infrastructure, Repository] },
        { "InsightFlow.QueryService", [Infrastructure, Repository, Foundation] },
        { "InsightFlow.AgentService", [Agent, Repository, Foundation] },
        { "InsightFlow.Api", [Logic, Infrastructure, Repository, Foundation] },
        { "InsightFlow.Worker", [Infrastructure, Repository, Foundation] },
        { "InsightFlow.MigrationService", [Repository, Foundation] },
        { "InsightFlow.Web", [DomainProject, Foundation] },
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
