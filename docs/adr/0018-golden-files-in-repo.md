# 0018. In-repo golden-file helper instead of Verify

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

The prompt suggested Verify for golden files. Verify's sponsorship check broke builds without a sponsor licence, so Roy chose an in-repo helper (option 1).

## Decision

- `tests/InsightFlow.Testing/Golden.cs`: `Golden.Match(text, name, ext)` / `Golden.MatchJson` compare against `Golden/{name}.verified.{ext}` next to the test. A mismatch writes `.received.{ext}` and fails with the first differing line.
- Accepting changes: review and rename, or rerun with `INSIGHTFLOW_ACCEPT_GOLDEN=1`. CI uploads `*.received.*` files on failure.

## Consequences

+ No external dependency, and the output is deterministic. - No diff-tool integration; reviewers read the files.
