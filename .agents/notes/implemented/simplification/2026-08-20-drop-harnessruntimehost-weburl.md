# Agent Note: Drop dead HarnessRuntimeHost.WebUrl state

Status: implemented

## Problem

`HarnessRuntimeHost.WebUrl` (`src/DeepSeek.Harness.Desktop/Services/HarnessRuntimeHost.cs:31` `public Uri? WebUrl {get;private set;}`) is written in `StartAsync`/`StartCoreAsync`/`RestartAsync` (8 matches, all in the same file) but never read in production: `Program.cs` uses the `Uri?` returned by `StartAsync`/`RestartAsync` (line 28, 192), `RuntimeSupervisor.cs` uses the `RestartAsync` return, `HarnessRuntimeHostTests.cs` never asserts `.WebUrl`. The property duplicates the return value and invites racy reads of stale mutable state.

## Decision

Delete `public Uri? WebUrl {get;private set;}` and all `WebUrl =` assignments (`HarnessRuntimeHost.cs` 7 lines). Keep the return-value contract (`StartAsync`/`RestartAsync` return `Uri?`). If post-timeout diagnostics need the last URL, keep a private `_lastUrl` or remove entirely. Update XML doc.

## Alternatives considered

Convenience for callers that don't want to handle the return value, or for supervisor/Program to poll the last URL. No caller ever polls; supervisor gets the URL via `RestartAsync` return and Program captures `webUrl` locally, so the convenience buys nothing and leaves stale state.

### Verification

* `grep -r "\.WebUrl" src tests scripts .github` returns 0.
* `dotnet build` and `dotnet test 28/28` green.
* `HarnessRuntimeHost` still exposes `StartAsync`/`RestartAsync` returning `Uri?`.

## Consequences

Low. Public API removal but proven 0 prod + 0 test consumers. Internal refactor only; no behavior change beyond removing stale mutable state.
