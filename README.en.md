# DeepSeek Harness Desktop for .NET

[中文](README.md) | English

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![build & test](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml/badge.svg)](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml)
![platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows%20%7C%20Linux-4f6ef7)
![.NET](https://img.shields.io/badge/.NET-net10.0-512bd4)

A **.NET desktop client for [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)** (MIT), built on [Ryn](https://github.com/Yupmoh/Ryn) — a Tauri-for-C# native-webview framework. The shell bundles the complete DeepSeek Harness runtime, so end users need **no separate Node.js or DeepSeek Harness installation**.

> Status: early-stage framework — native shell + bundled runtime + crash recovery + plugin capability verified; formal packaging/CI in progress.

## Highlights

- **Native, lightweight shell** — C# backend, HTML/CSS/JS frontend in the OS webview (WebView2 / WKWebView / WebKitGTK), NativeAOT-ready, deny-by-default capability sandbox (`ryn.json`).
- **Full runtime bundled** — `resources/runtime/` ships a Node binary + the `@deepseek-ai/dsh` dependency closure; `dsh web` is spawned by the shell with a private `DSH_HOME`, and the UI loads at `dsh web:` URL. No PATH `dsh`/`node` required (falls back to PATH `dsh` if the bundle is absent).
- **Crash recovery** — the shell supervises the runtime child: on exit it shows a recovery screen, restarts the child, and navigates the same window to the new URL. The port is kept stable so the Web UI's origin (and its in-page session memory) survives a restart — you return to your previous conversation.
- **Plugin ecosystem** — plugins install into the user's `DSH_HOME` profile (`dsh plugin --profile web add …`); `DSH_DESKTOP_PATCH` adds a desktop-side `--patch` overlay.
- **Testable host layer** — `HarnessRuntimeHost` / `RuntimeSupervisor` / URL parser are unit- and e2e-tested (xunit); gates scripted in `scripts/`.

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
├── src/DeepSeek.Harness.Desktop/   # Ryn shell: Program, HarnessRuntimeHost, RuntimeSupervisor, IPC commands
├── tests/…Tests/                   # xunit tests
├── resources/runtime/              # bundled Node + dsh closure (generated, gitignored)
├── scripts/                        # gate scripts + bundle-runtime.sh
└── docs/                           # project docs (to come)
```

## License & acknowledgements

- This project: MIT.
- [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) (MIT) — the runtime this shell hosts.
- [Ryn](https://github.com/Yupmoh/Ryn) (MIT) — the desktop shell framework.
- Packaging approach inspired by [pilot-harness](https://github.com/op7418/pilot-harness).
