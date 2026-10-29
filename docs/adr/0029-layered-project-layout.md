# 0029. Layered project layout (Domain, Foundation, Repository, Infrastructure, Agent, Logic, hosts, Tests)

- **Status:** Accepted
- **Date:** 2026-10-29
- **Deciders:** Abhishek (requested), Roy (tech lead) to review: this changes the architecture tests and the project graph.

## Context

The solution had fourteen production projects named after technologies (`Persistence`, `Connectors`, `Query`, `Contracts`,
`ServiceDefaults`...) and eight test projects. The sister product BPIL AI Console uses one recognisable layering: `Domain`, `Foundation`,
`Infrastructure`, `Repository`, `Agent`, `Logic`, one `Tests` project, a thin host, and one solution in `src/`. Teams moving between the two
products should find the same shape.

## Decision

Same behaviour, new layout (no feature changes):

| Before | After |
|---|---|
| `Domain` + `Contracts` | `InsightFlow.Domain` (the DTOs are now `Domain/Contracts`, namespace `InsightFlow.Domain.Contracts`) |
| `ServiceDefaults` | `InsightFlow.Foundation` |
| `Persistence` | `InsightFlow.Repository` |
| `Connectors` + `Query` | `InsightFlow.Infrastructure` (`Connectors/` and `Query/` folders, namespaces `InsightFlow.Infrastructure.*`) |
| `Agents` | `InsightFlow.Agent` |
| the services inside `Api` | `InsightFlow.Logic` (`Api` keeps only endpoints and `Program.cs`) |
| eight test projects | `InsightFlow.Tests` (folders `Domain`, `Infrastructure`, `Agent`, `Web`, `Architecture`, `Integration`) |
| `InsightFlow.slnx` at the root | `src/InsightFlow.slnx` |

- The deployable hosts (`Web`, `Api`, `QueryService`, `AgentService`, `Worker`, `MigrationService`, `AppHost`) stay separate: they are
  separate containers, which the single-host reference product does not have.
- Dependency rules: Domain and Foundation reference nothing; Repository and Infrastructure reference Domain; Agent references Domain and
  Infrastructure; Logic references Domain, Foundation, Infrastructure and Repository; Web references Domain (only `Contracts` and `Viz`) and
  Foundation. They are enforced by `src/InsightFlow.Tests/Architecture`.
- The end-to-end tests that start the AppHost carry `[Trait("Category", "Integration")]`, so CI runs them in their own job
  (`--filter-trait "Category=Integration"`) and the unit job excludes them (`--filter-not-trait`).
- `Evals`, `Benchmarks` and the shared `InsightFlow.Testing` helpers stay as separate tool projects (`evals/`, `tests/`); they are programs, not tests.
- EF Core migration ids do not depend on namespaces, so existing databases are unaffected.

## Consequences

+ One familiar shape across products; business logic is reachable from more than one host and testable without ASP.NET endpoints.
+ One test project, one test run, one place for fixtures.
- Logic references the ASP.NET Core shared framework (it throws `ApiProblemException` with HTTP status codes); it never references the UI.
- Merging `Query` and `Connectors` means DuckDB and the database drivers live in one assembly.
- Old links, branches and the `Contracts` namespace in open pull requests need updating.
