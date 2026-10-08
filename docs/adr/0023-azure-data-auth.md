# 0023. Azure data-plane authentication (Postgres password, Redis access key)

- **Status:** Accepted (interim)
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Azure PostgreSQL Flexible Server and Azure Managed Redis default to Entra token authentication in Aspire 13.6. Our clients use plain connection strings: EF Core and the Quartz ADO job store (Npgsql), and `AddRedisDistributedCache` (StackExchange.Redis).

## Decision

- In publish mode the AppHost calls `postgres.WithPasswordAuthentication(keyVault)` and `redis.WithAccessKeyAuthentication(keyVault)`. The generated connection strings are stored in Key Vault and injected as Container Apps secret references.
- Blob storage and Key Vault keep using managed identities.

## Consequences

+ Works with every client we use today, Quartz included.
- Passwords and keys exist (in Key Vault). TODO(dev2): move to Entra auth, using an `NpgsqlDataSource` with a periodic token provider (EF + Quartz connection provider) and `Aspire.Microsoft.Azure.StackExchangeRedis` with Azure authentication.
