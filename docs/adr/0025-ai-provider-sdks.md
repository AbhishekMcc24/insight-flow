# 0025. AI provider SDKs: OpenAI SDK for Azure OpenAI, official Anthropic SDK

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

The prompt suggested `Azure.AI.OpenAI` and "the official Anthropic SDK". `Azure.AI.OpenAI`'s stable line had stalled, and Microsoft now recommends the OpenAI SDK against the Azure `/openai/v1/` endpoint. The official Anthropic package id is `Anthropic`; `Anthropic.SDK` is a community package.

## Decision

- Azure OpenAI: `new ChatClient(deployment, ApiKeyCredential | BearerTokenPolicy(DefaultAzureCredential), new OpenAIClientOptions { Endpoint })` → `.AsIChatClient()` (`Microsoft.Extensions.AI.OpenAI`).
- Anthropic: `new AnthropicClient { ApiKey }.AsIChatClient(model)`.
- Both are registered as keyed `IChatClient`s behind the model router (ADR 0008).

## Consequences

+ Current, supported SDKs. - The OpenAI adapter API is marked evaluation-only (OPENAI001), suppressed locally with a comment.
