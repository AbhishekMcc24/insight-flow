using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchitectureModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace InsightFlow.Architecture.Tests;

/// <summary>
/// Enforces the dependency rules on compiled code: what each assembly actually references (reflection)
/// and which types it actually uses (ArchUnitNET).
/// </summary>
public sealed class AssemblyDependencyTests
{
    private static readonly Assembly DomainAssembly = Assembly.Load("InsightFlow.Domain");
    private static readonly Assembly ContractsAssembly = Assembly.Load("InsightFlow.Contracts");
    private static readonly Assembly QueryAssembly = Assembly.Load("InsightFlow.Query");
    private static readonly Assembly AgentsAssembly = Assembly.Load("InsightFlow.Agents");
    private static readonly Assembly ConnectorsAssembly = Assembly.Load("InsightFlow.Connectors");
    private static readonly Assembly PersistenceAssembly = Assembly.Load("InsightFlow.Persistence");
    private static readonly Assembly WebAssembly = Assembly.Load("InsightFlow.Web");

    private static readonly Lazy<ArchitectureModel> Architecture = new(() =>
        new ArchLoader()
            .LoadAssemblies(DomainAssembly, ContractsAssembly, QueryAssembly, AgentsAssembly, ConnectorsAssembly, PersistenceAssembly, WebAssembly)
            .Build());

    [Fact]
    public void Domain_ReferencesOnlyBcl_Pass()
    {
        var illegal = ReferencedAssemblyNames(DomainAssembly)
            .Where(n => !(n.StartsWith("System", StringComparison.Ordinal) || n is "netstandard" or "mscorlib"))
            .ToList();

        illegal.ShouldBeEmpty("InsightFlow.Domain is a pure model: BCL only");
    }

    [Fact]
    public void Query_DoesNotReferenceAspNetCoreOrAgentsOrUi_Pass()
    {
        var illegal = ReferencedAssemblyNames(QueryAssembly)
            .Where(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                        || n.StartsWith("InsightFlow.Agents", StringComparison.Ordinal)
                        || n.StartsWith("InsightFlow.Persistence", StringComparison.Ordinal))
            .ToList();

        illegal.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("InsightFlow.Domain")]
    [InlineData("InsightFlow.Contracts")]
    [InlineData("InsightFlow.Persistence")]
    [InlineData("InsightFlow.Query")]
    [InlineData("InsightFlow.Agents")]
    [InlineData("InsightFlow.Connectors")]
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
            "InsightFlow.Query", "InsightFlow.Agents", "InsightFlow.Persistence", "InsightFlow.Connectors",
            "DuckDB", "Npgsql", "Microsoft.EntityFrameworkCore", "Microsoft.Agents.AI", "OpenAI", "Anthropic",
        ];

        var illegal = ReferencedAssemblyNames(WebAssembly)
            .Where(n => forbiddenPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        illegal.ShouldBeEmpty("Web talks to services over HTTP only");
    }

    [Fact]
    public void Web_UsesOnlyVizContractTypesFromDomain_Pass()
    {
        // Web may use the shared chart contract (InsightFlow.Domain.Viz: VizSpec & friends, exposed through Contracts)
        // but no other Domain types (entities, rules, validators).
        var rule = Types().That().ResideInAssembly(WebAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^InsightFlow\.Domain(?!\.Viz(\.|$))")
            .WithoutRequiringPositiveResults();

        rule.Check(Architecture.Value);
    }

    [Fact]
    public void Query_DoesNotDependOnAgentTypes_Pass()
    {
        var rule = Types().That().ResideInAssembly(QueryAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^InsightFlow\.Agents(\.|$)")
            .WithoutRequiringPositiveResults();

        rule.Check(Architecture.Value);
    }

    private static IEnumerable<string> ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);
}
