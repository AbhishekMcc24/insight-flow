# 0003. Orchestration with Aspire 13 (D3)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

The system has six services plus PostgreSQL, Redis and Blob storage. We want one-command local runs, the same topology in Azure, and built-in telemetry.

## Decision

- `InsightFlow.AppHost` (`Aspire.AppHost.Sdk/13.6.1`) declares every resource. `InsightFlow.ServiceDefaults` adds OpenTelemetry, `/health` + `/alive`, the standard resilience handler and service discovery.
- Services wait for dependencies with `WaitFor`, and for the one-shot migration step with `WaitForCompletion(migrations)` (ADR 0020).
- Integration tests start the real AppHost through `Aspire.Hosting.Testing`. They set `InsightFlow:EphemeralInfrastructure=true` to get throwaway containers.

## Consequences

+ `dotnet run --project src/InsightFlow.AppHost` (or `aspire run`) starts everything, including the dashboard.
- Coupled to the Aspire release cadence (13.x). See ADR 0021 for how DCP is resolved.
