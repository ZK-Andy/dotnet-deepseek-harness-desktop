# DeepSeek Harness Desktop for .NET

[中文](README.md) | English

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![build & test](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml/badge.svg)](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml)
[![tests](https://img.shields.io/badge/tests-25%2F25-brightgreen)](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml)
[![coverage](https://img.shields.io/badge/coverage-26.4%25-orange)](docs/testing.md)
[![platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows%20%7C%20Linux-4f6ef7)](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases)
[![.NET](https://img.shields.io/badge/.NET-net10.0-512bd4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![docs](https://img.shields.io/badge/docs-architecture%2Ftesting%2Fdevelopment-blue)](docs/architecture.md)

A **.NET desktop client for [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)** (MIT), built on [Ryn](https://github.com/Yupmoh/Ryn) — a Tauri-for-C# native-webview framework. The shell bundles the complete DeepSeek Harness runtime, so end users need **no separate Node.js or DeepSeek Harness installation**.

> Status: `v0.1.12` (`25/25` tests `26.4%` coverage + gates green, `CI` dual-green) — native shell + bundled runtime + crash recovery + market closed; `Wayland`/icon fixed, `0.1.11` ships `497K` real `tgz` + background `JSON` detection/migration/`allowBuilds` self-heal, `0.1.12` auto-restarts to show market.

## Highlights

- **Native, lightweight shell** — C# backend, HTML/CSS/JS frontend in the OS webview (WebView2 / WKWebView / WebKitGTK), NativeAOT-ready, deny-by-default capability sandbox (`ryn.json`).
- **Full runtime bundled** — `resources/runtime/` ships a Node binary + the `@deepseek-ai/dsh` dependency closure (`pilot-harness` whole-tree `node_modules`, `--store-dir` avoids `sqlite` lock); `dsh web` is spawned by the shell with a private `DSH_HOME`, and the UI loads at `dsh web:` URL. No PATH `dsh`/`node` required (falls back to PATH `dsh` if the bundle is absent).
- **Crash recovery** — the shell supervises the runtime child: on exit it shows a recovery screen, restarts the child, and navigates the same window to the new URL. The port is kept stable so the Web UI's origin (and its in-page session memory) survives a restart — you return to your previous conversation.
- **Plugin market pre-installed** — `dsh-market` (`https://github.com/dsh-market/dsh-market`) ships as `497K` real `tgz` in `resources/runtime/dshmarket.tgz` — first launch background `file://` installs to `DSH_HOME` (`System.Text.Json` exact check, `app` bogus migration, `pnpm-workspace.yaml` 6 `allowBuilds` self-heal) and `RuntimeSupervisor` restarts to show, `1200+` plugins searchable one-click (`0.1.8` blocking→`0.1.11` real `tgz`→`0.1.12` immediate restart).
- **Native icon** — `assets/icon.png` (`hairyf/deepseek-harness-desktop` `512`) ships with the package, installed to `hicolor` and referenced as `Icon=deepseek-harness-desktop` in the `.desktop` entry; `ryn.json:identifier` and `StartupWMClass` are both `io.github.ZK-Andy.dotnet-deepseek-harness-desktop` for correct `Wayland`/`X11` taskbar association.
- **Testable host layer** — `HarnessRuntimeHost` / `RuntimeSupervisor` / `RuntimeLocator` / `MarketInstallHelper` / URL parser are xunit `25/25` (`MarketInstallHelper 84%`) and gate-checked; `package-linux.sh` `fail loud` verifies real `tgz` in `staging`.

## Platform Packages

Referencing [Ryn](https://github.com/Yupmoh/Ryn)'s `ryn bundle` (`macOS .app` / `Windows` folder + `WiX` / `Linux AppDir`) and `release.yml` matrix (`osx-arm64`/`linux-x64`/`win-x64` each on native `OS`), this project with `PublishAot=false` can cross-compile, so both `macOS` archs use single `macos-latest` runner (matrix by `rid`, `ARM via Rosetta`).

| Platform | Arch | Package | Runner | Test | Client |
|---|---|---|---|---|---|
| `Linux` | `x64` | `deb`/`rpm` | `ubuntu-latest` | ✅ `CI` (`staging` + `rpm -qp`) | 🟡 `deb` / 🟢 `rpm` |
| `Linux` | `arm64` | `deb`/`rpm` | `ubuntu-24.04-arm` | ✅ `CI` (matrix) | 🟡 `deb` / 🟢 `rpm` |
| `macOS` | `arm64` | `zip` (`.app`) | `macos-latest` | ✅ `CI` (single runner) | 🟡 |
| `macOS` | `x64` | `zip` (`.app`) | `macos-latest` | ✅ `CI` (cross) | 🟡 |
| `Windows` | `x64` | `zip` | `windows-latest` | ✅ `CI` | 🟡 |

`Linux` is the `rpm` anchor verifiable via `ARCH=arm64 bash scripts/package-linux.sh --stage-only` locally; `mac/win` are `tag+workflow_dispatch` manual, `CI` publishes `SHA256SUMS` together.

> 🟢 Tested specifically (`rpm` verifiable via `rpm -qp --requires`), 🟡 Implemented but not specifically tested (`CI` auto only).

## Quick start (development)

```sh
# prerequisites: .NET 10 SDK; on Linux: WebKitGTK
# optional: pre-bundle the runtime from a local dsh install (needs node + @deepseek-ai/dsh)
scripts/bundle-runtime.sh

# run — uses PATH dsh by default; use bundled runtime with:
# DSH_DESKTOP_RUNTIME_DIR=$PWD/resources/runtime
dotnet run --project src/DeepSeek.Harness.Desktop

# tests
dotnet test dotnet-deepseek-harness-desktop.slnx

# webview devtools (default off)
DSH_DEVTOOLS=1 dotnet run --project src/DeepSeek.Harness.Desktop
```

> The runtime needs a reachable DeepSeek provider credential (e.g. `DEEPSEEK_API_KEY`) to serve the Web UI.

## Layout

```
├── src/DeepSeek.Harness.Desktop/   # Ryn shell: Program (background market+restart), Services/HarnessRuntimeHost, RuntimeSupervisor, RuntimeLocator, MarketInstallHelper, HarnessUrlParser
├── tests/DeepSeek.Harness.Desktop.Tests/  # xunit 25/25
├── resources/runtime/              # bundled Node + dsh closure + dshmarket.tgz 497K (generated, gitignored, staging verifies real tgz)
├── scripts/                        # gates + bundle-runtime-ci.sh (bundle-runtime.sh is wrapper) + package-linux.sh
├── .agents/notes/implemented/bug-fix/2026-08-20-dshmarket-background-install.md  # market chain ADR
└── docs/architecture.md, testing.md, development.md  # project docs
```

## License & acknowledgements

- This project: MIT.
- [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) (MIT) — the runtime this shell hosts.
- [Ryn](https://github.com/Yupmoh/Ryn) (MIT) — the desktop shell framework.
- Packaging approach inspired by [pilot-harness](https://github.com/op7418/pilot-harness).
