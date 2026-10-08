# 0020. Dedicated migration service

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

If every service migrated the database at startup, replicas would race each other on schema changes.

## Decision

- `InsightFlow.MigrationService` applies EF migrations (and development seed data in Development) once. The other services use `WaitForCompletion(migrations)`. A failure sets a non-zero exit code.
- In Azure Container Apps (no jobs in Aspire 13.6) it runs with `Migrations__KeepAlive=true`: it migrates, then idles instead of exiting, so the platform doesn't restart it in a loop.

## Consequences

+ One writer of the schema. - In ACA, services may start before migrations finish and are restarted until the schema is ready. TODO(dev2): switch to an ACA Job when supported.
