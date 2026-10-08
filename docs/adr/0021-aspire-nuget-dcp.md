# 0021. Resolve DCP and the dashboard from NuGet

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Aspire 13.6 can use DCP from the installed CLI bundle or from NuGet packages. CI, and `Aspire.Hosting.Testing`, need deterministic versions without an installed CLI. The build also emits the advisory ASPIRE010, which `TreatWarningsAsErrors` turns into an error.

## Decision

- `AspireUseCliBundle=false` in the AppHost; `ASPIRE010` is suppressed (it only advertises the bundle).
- Run locally with `dotnet run --project src/InsightFlow.AppHost` (or `aspire run`).

## Consequences

+ The same orchestrator version everywhere.
- Troubleshooting: DCP refuses to start if `%USERPROFILE%/.dcp/state.elevated` is owned by another principal (seen after an elevated run on Windows). Delete that folder; DCP recreates it.
