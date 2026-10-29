// The tests used to sit in namespaces nested under the assemblies they cover (InsightFlow.Domain.Tests, InsightFlow.Agent.Tests, ...) and saw
// those assemblies' root types without a using. They now share one assembly, so the root namespaces are imported here.
global using InsightFlow.Domain;
