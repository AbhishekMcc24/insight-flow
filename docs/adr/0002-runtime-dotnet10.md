# 0002. Runtime: .NET 10, C# 14 (D2)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

We need a long-term-support runtime with modern C#. Warnings must not pile up while two developers and AI sessions write code.

## Decision

- .NET 10 SDK, pinned in `global.json` (`10.0.401`, `rollForward: latestFeature`); C# 14; nullable enabled.
- `Directory.Build.props` sets `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, `EnforceCodeStyleInBuild` and `GenerateDocumentationFile` (CS1591 is suppressed, so public members don't each need a doc comment; public abstractions still document *why*).
- Central Package Management (`Directory.Packages.props`) and the `.slnx` solution format.
- Tests run on Microsoft.Testing.Platform (`global.json` → `"test": { "runner": "Microsoft.Testing.Platform" }`). VSTest is not supported with xUnit v3 on .NET 10 here.

## Consequences

+ Analyzer findings fail the build, so the code stays consistent.
- Some analyzer rules (e.g. CA1859, IDE0005) occasionally need small refactors; that is the intended cost.
