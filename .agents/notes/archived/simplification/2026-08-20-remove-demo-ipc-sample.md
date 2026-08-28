# Agent Note: Remove demo IPC sample (GreetingService + wwwroot hello)

Status: implemented
Archived: 2026-08-28

## Problem

`GreetingService` (`src/DeepSeek.Harness.Desktop/Services/GreetingService.cs`) and `AppCommands` (`Commands/AppCommands.cs` `app.hello`) plus `wwwroot/index.html` hello button and `tests/GreetingServiceTests.cs` are demo-only. Production callers 2 (definition + delegation) vs test/docs 4; `Program.cs` registers `AddAppCommands` but normal path (`webUrl != null`) never serves `wwwroot`, so the `app.hello` surface is dead when `dsh` is healthy. The fallback page's purpose is liveness, not a demo.

## Decision

Delete `Services/GreetingService.cs`, `Commands/AppCommands.cs` (and `Commands/` if empty), `tests/GreetingServiceTests.cs`, and the `wwwroot` hello card (replace with static `dsh not available` showing `StderrTail`). Remove `services.AddAppCommands()` (keep `AddRynCommands` if other commands remain). Drop the `GreetingServiceTests` bullet from `docs/testing.md` and update `README` dir tree. Keep a one-line IPC example in `docs/development.md` if onboarding needs it.

## Alternatives considered

Minimal runnable proof that `Ryn.Ipc.Generator` + WebView bridge works; new contributors copy it.

### Verification

* `grep -r "GreetingService|app\.hello" src tests` returns 0.
* `dotnet build` and `dotnet test` still `28-3=25` (or updated count) green.
* Fallback `wwwroot/index.html` is static, no `window.__ryn.invoke`.

## Consequences

Low. No product feature depends on it. Loss of onboarding sample mitigated by doc example.
