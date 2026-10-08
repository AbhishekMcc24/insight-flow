# 0011. Query result cache in Redis (D11)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Dashboards re-issue identical queries, and extracts are immutable, so results for a given dataset version never change.

## Decision

- `DistributedQueryCache` stores `QueryResult` in Redis (`IDistributedCache`) under `QueryCacheKey` = SHA-256(key version, tenant, dataset version, dialect, semantic-model fingerprint, canonical `VizSpec` JSON, time bucket for relative dates). Default TTL: 1 hour (`QueryEngineOptions.CacheTtl`).
- The response reports `FromCache`; the Web shows a "cached" badge.

## Consequences

+ A repeat query costs about a millisecond plus a network hop. No invalidation is needed, because a new extract is a new version.
- Specs with relative dates include a time bucket in the key, so "last 3 months" rolls over. A model change produces a new fingerprint, so stale results are not served.
