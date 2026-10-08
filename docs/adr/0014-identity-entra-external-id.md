# 0014. Identity: Entra External ID; development handler locally (D14)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Customers sign in with their own identities. Local development and tests must run without an identity provider.

## Decision

- Cloud APIs: `AddMicrosoftIdentityWebApi` (JWT bearer). Web app: `AddMicrosoftIdentityWebApp` (OIDC + cookie) with token acquisition, so it calls the APIs as the user (`AddInsightFlowWebSecurity`).
- Locally: `DevelopmentAuthenticationHandler` with an optional `Authorization: Dev <base64url-json>` token. It defaults to the seeded Contoso Retail user. It refuses to register, and to authenticate, outside the Development environment.
- The tenant comes from a configurable claim (`tenant_id`). The fallback policy rejects requests without a valid tenant; health endpoints and static assets are anonymous.
- Roles Viewer < Explorer < Creator < TenantAdmin; policies CanView, CanExplore, CanCreate and CanAdministerTenant are defined in `InsightFlowPolicies`.

## Consequences

+ Integration tests impersonate any tenant/role with Dev tokens.
- TODO(dev2): verify the Entra configuration against a real tenant, implement the `tenant_id` claim, tenant onboarding and role-management screens.
