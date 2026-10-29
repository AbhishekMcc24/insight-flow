// The connector tests used to live in the Connectors assembly's namespace and saw its types without a using; they now sit beside the other
// layers' tests, so the connector namespaces are imported once here.
global using InsightFlow.Infrastructure.Connectors;
global using InsightFlow.Infrastructure.Connectors.Databases;
global using InsightFlow.Infrastructure.Connectors.Documents;
global using InsightFlow.Infrastructure.Connectors.Extraction;
global using InsightFlow.Infrastructure.Connectors.Files;
global using InsightFlow.Infrastructure.Connectors.Knowledge;
global using InsightFlow.Infrastructure.Connectors.Stubs;
