# 0027. Web BFF proxy for uploads/downloads; services stay internal

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Large files must not travel over the Blazor Server SignalR circuit, and the Api must not be exposed to browsers.

## Decision

- The Web hosts `/bff/workspace/folders/{id}/files` (streaming multipart proxy) and `/bff/workspace/items/{id}/content` (streamed download). They call the Api with the user's credentials through typed `HttpClient`s (service discovery). The upload client has no resilience handler, because a streamed body cannot be retried.
- CSRF: state-changing BFF calls require the custom header `X-InsightFlow-Request` and a same-origin `Origin`. The antiforgery form token is not used because validating it would read the multipart body.
- `ServiceCallCredentials` puts the user's identity on every service call: a rebuilt Dev token in Development, an Entra access token in the cloud.

## Consequences

+ Only the Web is internet-facing, and uploads stream end to end.
- CORS must stay disabled on the Web app for the header-based CSRF defence to hold.
