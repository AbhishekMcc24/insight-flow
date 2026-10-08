# 0019. Key Vault only in publish mode; local secret store in development

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Key Vault has no emulator. Adding it in run mode would provision real Azure resources on `aspire run`.

## Decision

- The AppHost adds `AddAzureKeyVault("keyvault")` only when `ExecutionContext.IsPublishMode`. Services reference it through `WithKeyVault`.
- `ISecretStore` is `KeyVaultSecretStore` in the cloud and `LocalFileSecretStore` locally (`%LOCALAPPDATA%/InsightFlow/secrets.local.json`, outside the repo).
- AI keys are optional Aspire parameters (`WithOptionalParameter`): user secrets locally, secure parameters when deploying.

## Consequences

+ `aspire run` never touches a subscription. - Secret-store behaviour differs between local and cloud; covered by `SecretStoreTests` (local) and a manual check in Azure.
