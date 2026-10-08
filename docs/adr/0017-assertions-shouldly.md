# 0017. Assertion library: Shouldly

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

The prompt allowed FluentAssertions or Shouldly. FluentAssertions 8.x requires a paid commercial licence.

## Decision

Shouldly 4.x, imported globally for all `*Tests` projects (`tests/Directory.Build.props`). Test names follow `Method_State_Expected`.

## Consequences

+ No licence risk. - Slightly different style from FluentAssertions for developers used to it.
