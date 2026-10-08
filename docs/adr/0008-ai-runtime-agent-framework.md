# 0008. AI runtime: Agent Framework on IChatClient (D8)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

We need provider choice (Azure OpenAI and Anthropic, no local LLMs), per-task model selection, cost control and testability.

## Decision

- `Microsoft.Agents.AI` 1.x: `chatClient.AsAIAgent(instructions, name, tools)`, `RunStreamingAsync` and sessions.
- Providers are keyed `IChatClient`s (see ADR 0025 for the SDKs). Each is wrapped with `ChatClientBuilder` middleware: OpenTelemetry, logging (metadata only) and `TokenBudgetChatClient` (per-tenant monthly caps).
- `IModelRouter.Resolve(tenant, AiTask)` reads routes from configuration; `AiTask` is Routing, SqlGeneration, ChartRecommendation or InsightSummary. Providers without credentials are disabled, and the app still starts.
- Analyst agent tools: `DescribeModel`, `RunSql` (sandbox) and `ProposeChart` (validated `VizSpec`). The derived-field flow is `DerivedFieldPlanner`. AgentService streams Server-Sent Events.

## Consequences

+ Unit tests use a scripted fake `IChatClient`; real-provider tests run only when keys are present.
- Token usage is kept in the distributed cache (`DistributedCacheTokenUsageStore`). TODO(dev2): persist it in PostgreSQL for billing and admin reports.
