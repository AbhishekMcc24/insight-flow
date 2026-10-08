# 0015. Background jobs with Quartz.NET (D15)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Extracts and future schedules (refresh, subscriptions, alerts) must survive restarts and run once across replicas.

## Decision

- Quartz.NET 4.3 in `InsightFlow.Worker`: PostgreSQL ADO job store with clustering, the System.Text.Json serializer and schema provisioning.
- `ExtractRunProcessor` claims queued `ExtractRun`s with `FOR UPDATE SKIP LOCKED`; `ExtractRefreshJob` drives it.
- Quartz 4 API notes: `IJob.Execute(context, ct)` returns `ValueTask`; `AddQuartz(name, …)`; `GenerateInstanceId`.

## Consequences

+ Safe to run several Worker replicas.
- Quartz owns `qrtz_*` tables beside the EF schema. TODO(dev2): cron schedules per ExtractDefinition.
