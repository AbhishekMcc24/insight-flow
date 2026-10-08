## What and why

<!-- One or two sentences. Link the issue / ADR if there is one. -->

## How it was verified

<!-- Commands run, tests added, screenshots for UI changes. -->

## Checklist

- [ ] Builds with **zero warnings** (`dotnet build InsightFlow.slnx`)
- [ ] Tests added/updated and green (`dotnet test --solution InsightFlow.slnx`); golden files reviewed, not blindly accepted
- [ ] **Tenant isolation considered:** new data is `ITenantOwned` / filtered by tenant; new endpoints have a cross-tenant test
- [ ] Authorization uses `InsightFlowPolicies` (no role strings in endpoints)
- [ ] **No secrets** in code, config or tests; credentials only as `SecretReference`
- [ ] No row data, data-bearing prompts or credentials in logs
- [ ] AI-generated SQL (if any) runs only through `ISqlSandbox`; no Python
- [ ] Dependency rules respected (architecture tests pass); no UI/JS outside Web; no CDN scripts
- [ ] `CancellationToken` passed through every new async path
- [ ] New DTOs added to `ContractsJsonContext`; new options validated on start
- [ ] Docs/ADR updated if a decision or a contract changed; `TODO(dev2)`/`TODO(roy)` markers added for deferred work
- [ ] Conventional Commit title (`feat(scope): …`, `fix(scope): …`, …)
