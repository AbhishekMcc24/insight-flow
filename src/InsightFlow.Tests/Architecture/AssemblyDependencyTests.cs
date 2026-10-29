using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchitectureModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace InsightFlow.Tests.Architecture;

/// <summary>
/// Enforces the dependency rules on compiled code: what each assembly actually references (reflection)
/// and which types it actually uses (ArchUnitNET).
/// </summary>
public sealed class AssemblyDependencyTests
{
    private static readonly Assembly DomainAssembly = Assembly.Load("InsightFlow.Domain");
    private static readonly Assembly InfrastructureAssembly = Assembly.Load("InsightFlow.Infrastructure");
    private static readonly Assembly AgentAssembly = Assembly.Load("InsightFlow.Agent");
    private static readonly Assembly RepositoryAssembly = Assembly.Load("InsightFlow.Repository");
    private static readonly Assembly LogicAssembly = Assembly.Load("InsightFlow.Logic");
    private static readonly Assembly WebAssembly = Assembly.Load("InsightFlow.Web");

    private static readonly Lazy<ArchitectureModel> Architecture = new(() =>
        new ArchLoader()
            .LoadAssemblies(DomainAssembly, InfrastructureAssembly, AgentAssembly, RepositoryAssembly, LogicAssembly, WebAssembly)
            .Build());

    [Fact]
    public void Domain_ReferencesOnlyBcl_Pass()
    {
        var illegal = ReferencedAssemblyNames(DomainAssembly)
            .Where(n => !(n.StartsWith("System", StringComparison.Ordinal) || n is "netstandard" or "mscorlib"))
            .ToList();

        illegal.ShouldBeEmpty("InsightFlow.Domain is a pure model and contract layer: BCL only");
    }

    [Fact]
    public void Infrastructure_DoesNotReferenceAspNetCoreOrAgentOrRepository_Pass()
    {
        var illegal = ReferencedAssemblyNames(InfrastructureAssembly)
            .Where(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                        || n.StartsWith("InsightFlow.Agent", StringComparison.Ordinal)
                        || n.StartsWith("InsightFlow.Repository", StringComparison.Ordinal)
                        || n.StartsWith("InsightFlow.Logic", StringComparison.Ordinal))
            .ToList();

        illegal.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("InsightFlow.Domain")]
    [InlineData("InsightFlow.Repository")]
    [InlineData("InsightFlow.Infrastructure")]
    [InlineData("InsightFlow.Agent")]
    [InlineData("InsightFlow.Logic")]
    public void Libraries_DoNotReferenceUiFrameworks_Pass(string assemblyName)
    {
        var illegal = ReferencedAssemblyNames(Assembly.Load(assemblyName))
            .Where(n => n.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal)
                        || n.StartsWith("Telerik", StringComparison.Ordinal))
            .ToList();

        illegal.ShouldBeEmpty("only InsightFlow.Web contains UI");
    }

    [Fact]
    public void Web_DoesNotReferenceServerInternals_Pass()
    {
        string[] forbiddenPrefixes =
        [
            "InsightFlow.Infrastructure", "InsightFlow.Agent", "InsightFlow.Repository", "InsightFlow.Logic",
            "DuckDB", "Npgsql", "Microsoft.EntityFrameworkCore", "Microsoft.Agents.AI", "OpenAI", "Anthropic",
        ];

        var illegal = ReferencedAssemblyNames(WebAssembly)
            .Where(n => forbiddenPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        illegal.ShouldBeEmpty("Web talks to services over HTTP only");
    }

    [Fact]
    public void Web_UsesOnlyContractAndVizTypesFromDomain_Pass()
    {
        // Web may use the shared contracts (InsightFlow.Domain.Contracts) and the chart contract (InsightFlow.Domain.Viz: VizSpec & friends)
        // but no other Domain types (entities, rules, validators).
        var rule = Types().That().ResideInAssembly(WebAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^InsightFlow\.Domain(?!\.(Viz|Contracts)(\.|$))")
            .WithoutRequiringPositiveResults();

        rule.Check(Architecture.Value);
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnAgentTypes_Pass()
    {
        var rule = Types().That().ResideInAssembly(InfrastructureAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^InsightFlow\.Agent(\.|$)")
            .WithoutRequiringPositiveResults();

        rule.Check(Architecture.Value);
    }

    [Fact]
    public void LowerLayers_DoNotDependOnLogicTypes_Pass()
    {
        // Logic sits on top: Domain, Repository, Infrastructure and Agent never call back into it.
        var rule = Types().That().ResideInAssembly(DomainAssembly)
            .Or().ResideInAssembly(RepositoryAssembly)
            .Or().ResideInAssembly(InfrastructureAssembly)
            .Or().ResideInAssembly(AgentAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^InsightFlow\.Logic(\.|$)")
            .WithoutRequiringPositiveResults();

        rule.Check(Architecture.Value);
    }

    private static IEnumerable<string> ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);
}
