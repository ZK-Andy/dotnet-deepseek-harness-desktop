<p align="center">
  <img src="assets/icon.png" width="96" alt="DeepSeek Harness Desktop for .NET">
</p>

# DeepSeek Harness Desktop for .NET

<p align="center"><a href="README.md">中文</a> · <strong>English</strong></p>

<p align="center"><strong>A .NET desktop shell for DeepSeek Harness — the interaction and the models live in the global dsh; the shell only owns windows, supervision and updates.</strong></p>

<p align="center">
  <a href="#features">Features</a> ·
  <a href="#download-and-install">Download</a> ·
  <a href="docs/user-guide.en.md">User Guide</a> ·
  <a href="docs/faq.en.md">FAQ</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="#development">Development</a> ·
  <a href="#documentation">Docs</a> ·
  <a href="LICENSE">MIT License</a>
</p>

<p align="center">
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases"><img src="https://img.shields.io/github/v/release/ZK-Andy/dotnet-deepseek-harness-desktop?style=flat&label=release&color=4D6BFE" alt="release"></a>
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases"><img src="https://img.shields.io/github/downloads/ZK-Andy/dotnet-deepseek-harness-desktop/total?style=flat&label=downloads&color=4D6BFE" alt="downloads"></a>
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop"><img src="https://img.shields.io/github/stars/ZK-Andy/dotnet-deepseek-harness-desktop?style=flat&label=stars&color=4D6BFE" alt="stars"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT"></a>
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml"><img src="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml/badge.svg" alt="build &amp; test"></a>
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/actions/workflows/ci.yml"><img src="https://img.shields.io/badge/tests-1123%2F1123-brightgreen" alt="tests"></a>
  <a href="docs/testing.md"><img src="https://img.shields.io/badge/coverage-67.55%25-yellowgreen" alt="coverage"></a>
  <a href="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases"><img src="https://img.shields.io/badge/platform-macOS%20%7C%20Windows%20%7C%20Linux-4f6ef7" alt="platform"></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/10.0"><img src="https://img.shields.io/badge/.NET-net10.0-512bd4" alt=".NET"></a>
</p>

A .NET desktop client for [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) (MIT). The host is [Avalonia](https://avaloniaui.net/) 12.1.x plus the package's native WebView (reusing the existing dsh spawn and loopback proxy): tray, close-to-tray and second-launch recall use Avalonia controls, and startup failures surface as an in-process modal; `window.__shell` carries page commands, events and per-load scripts, with external-link interception wired; the title bar supports native drag/resize on all three platforms (Wayland keeps native decorations); autostart, close-to-tray, diagnostics export, restart and the three update commands are wired, with quit/restart sharing one exit pipeline; crash supervision, first-run bootstrap, the in-page recovery page and the version-floor/unclean-exit banners are wired, and check results recall the main window before landing in an in-page banner. The shell ships no runtime: node and dsh are the system-global ones (`npm install -g @deepseek-ai/dsh@next`), and the desktop shares one data directory with the terminal, so sessions, credentials and plugins interoperate. On first launch it probes the global dsh on `PATH` — installs it when missing; when the probed version is below the compatibility floor it bootstraps an update to `@next`; otherwise it uses it directly (the criterion is the compatibility floor, not a per-run npm dist-tag lookup).

<p align="center">
  <a href="assets/screenshot.png"><img src="assets/screenshot.png" width="880" alt="DeepSeek Harness Desktop window showing the plugin market"></a>
</p>

<p align="center"><sub>Plugin market (<code>dshmarket</code>, which you install from within dsh; shown with 5 plugins installed)</sub></p>

## Why a shell

- **No runtime inside** — the installer carries the shell only (~30MB class); there is no second runtime closure and no private `PATH`, and node stays system-global.
- **Nothing pinned** — dsh follows the `@next` stable channel, so an upstream release takes effect immediately instead of waiting for a desktop release.
- **One data directory** — sessions, credentials and workspaces in `~/.dsh` are shared with the CLI, TUI and Web UI; desktop plugins occupy only `profiles/dotnet-desktop`.
- **No host impersonation** — everything runs in the OS webview, so pages and plugin behavior match the browser, and dsh plugins are unaffected.
- **No bundled market** — you install `dshmarket` from within dsh (`1200+` plugins searchable and one-click installable); its plugin updates never wait for a desktop release.

## Features

**Shell and runtime**

- ⚡️ **Online-first first launch** — installs when missing through the global node, bootstraps an update to `@next` when the probed version is below the compatibility floor, uses it directly otherwise; the node check runs once, only when dsh must be ensured.
- ⌨️ **CLI shim** — cleans up legacy self-generated pnpm shim leftovers on startup (only marked or dangling ones; user files untouched) and exposes the system-global node bin dir to the terminal `PATH`; pnpm itself is installed on demand by the bundled-plugin preflight (used if present), and dsh works from the terminal.
- 🔒 **Native and lightweight** — C# backend in the OS webview (WebView2 / WKWebView / WebKitGTK), shipped as JIT (NativeAOT is not enabled), deny-by-default command allow-list (unregistered commands are rejected).

**Window and system integration**

- 🪟 **Frameless window** — all three platforms drop the native title bar for a shell-drawn one: traffic lights top left, seamless sidebar clearance, inline collapse toggle, draggable top strip, double-click zoom.
- 🖥️ **System tray** — closing the window minimizes to the tray by default (switchable to quit in Settings); the menu offers show main window / check for updates (when the self-update stack is available) / restart / quit, labelled in the host UI language and rebuilt when the language changes at runtime; quit and restart share one orderly pipeline that reaps the runtime cleanly.
- 🪟 **Window state memory** — the last size and maximized state are restored on restart (already kept across tray hide/recall).
- 🚀 **Single-instance activation** — clicking the launcher again raises the existing window instead of spawning a duplicate (enforced on `Linux` / `macOS`; `Windows` does not arbitrate, so a second launch starts a new instance).
- 🔁 **Launch at login** — one-click toggle in Settings (XDG autostart / registry Run / LaunchAgents).
- 🖼️ **Icons and Wayland** — icons ship into `hicolor`, and `identifier` and `StartupWMClass` are the same value, so the taskbar associates correctly.

**Runtime self-healing and updates**

- 🔄 **Crash self-heal, back to the same conversation** — crash → failure surface (it appears only once the crash streak reaches the threshold, 3 by default; native dialog first: cause, stderr tail, exported diagnostics, keep or quit) → auto-restart → the same window returns to your conversation; **the port stays stable**, so origin and page-level session memory survive.
- 🧩 **Successor adoption** — a plugin-triggered self-restart is adopted by the shell: one process, one navigation, no doomed competing launch or tens-of-seconds recovery screen.
- ⬆️ **Self-update** — one background check at launch plus a manual check in Settings, then one-click install and restart (`SHA256`-verified packages; `macOS` guides a manual update). See [Staying current](#staying-current).

> When the preferred port is held by a program the app cannot attribute to itself, the launch drifts to a new port and the origin changes — consequences and recovery are in the [FAQ](docs/faq.en.md). Drift criteria, adoption lineage and probe budgets are documented in [docs/architecture.md](docs/architecture.md).

## Download and install

Download the package for your platform from [Releases](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases) (with `SHA256SUMS.txt`):

| Platform | Arch | Package | First launch |
|---|---|---|---|
| `Linux` | `x64` / `arm64` | `deb` (`…_linux-amd64.deb` / `…_linux-arm64.deb`), `rpm` (`…_linux-x86_64.rpm` / `…_linux-aarch64.rpm`) | `sudo apt install ./…deb` or `sudo dnf install ./…rpm` (dependencies resolved by the package manager) |
| `macOS` | `arm64` / `x64` | `dmg` | Drag into Applications and launch; if Gatekeeper blocks it, right-click → Open, or System Settings → Privacy & Security → Open Anyway |
| `Windows` | `x64` | `exe` installer (`…-setup.exe`) | Run the installer; on SmartScreen choose More info → Run anyway |

> **Unsigned**: this open-source project does not pay for signing, so releases are unsigned — the allow-through steps above exist for that. For dev/internal use, `SELF_SIGN=1` clears the local warning; see [docs/development.md](docs/development.md).
>
> **Preview**: the project is in an early preview stage and may ship breaking changes (data directory layout, configuration format, runtime component versions). **Cross-version data compatibility is not guaranteed**; back up important sessions and configuration before upgrading.
>
> **Upgrade**: the desktop profile directory is `profiles/dotnet-desktop` — an old `profiles/desktop` is renamed automatically on first launch (plugins and port memory kept); use `dsh --profile dotnet-desktop` in the terminal. See [docs/user-guide.en.md](docs/user-guide.en.md).
>
> **Real-machine validation**: acceptance has only been done on `Linux` `x64` `rpm`; `deb`, `Linux arm64`, `macOS` and `Windows` are built, packaged and smoke-tested by CI but unvalidated on real hardware, and `macOS x64` / `Windows` testing awaits community support. Boundaries are listed in [docs/testing.md](docs/testing.md).

## Staying current

- New versions are checked once in the background at launch (no polling, no interruptions); you can also check manually under **Settings → Desktop Settings** or from the tray menu. Dev runs do not load the self-update stack by default: Desktop Settings says the update stack is unavailable in that runtime and the tray menu omits **Check for updates**; `DSH_DESKTOP_UPDATE_FORCE=1` loads it explicitly.
- When a new version is found, a download button appears at the bottom of the sidebar — click it to download, verify and **install & restart in one click** (one system authorization prompt on `Linux`).
- Packages are strictly verified against `SHA256SUMS.txt` before installation; a failed check refuses to install.
- `macOS` does not support silent in-app updates yet — you are guided to download the `dmg` manually.

## How it works

The desktop shell (C# + OS webview) serves the Web UI of the global dsh on `PATH` through a loopback proxy: crash supervision restarts on the same port, first launch bootstraps global node/dsh, and the bundled companion is in place before spawn. Startup model and layering: [docs/architecture.md](docs/architecture.md) (single source of truth).

## Development

```sh
# prerequisites: .NET 10 SDK; on Linux: GTK3 + WebKitGTK 4.1
# run (dev environments must set DSH_DESKTOP_DEV=1 — isolated home + .dev app id;
# without a PATH dsh the first-launch bootstrap runs, which needs network)
DSH_DESKTOP_DEV=1 dotnet run --project src/DeepSeek.Harness.Desktop

# tests
dotnet test dotnet-deepseek-harness-desktop.slnx

# webview devtools (default off)
DSH_DEVTOOLS=1 dotnet run --project src/DeepSeek.Harness.Desktop
```

> The runtime needs a reachable DeepSeek provider credential (e.g. `DEEPSEEK_API_KEY`) to serve the Web UI. Packaging, dev isolation and the gate list are in [docs/development.md](docs/development.md) / [docs/testing.md](docs/testing.md).

## Layout

```
├── src/DeepSeek.Harness.Desktop/              # shell (Presentation): Avalonia host + assembly surface + page bridge
├── src/DeepSeek.Harness.Desktop.Core/         # pure logic, zero outer references
├── src/DeepSeek.Harness.Desktop.Infrastructure/  # adapters (native, dsh process, file/network, updates)
├── tests/                                     # xunit: shell / Core / Infrastructure / analyzer contract
├── tools/DeepSeek.Harness.Desktop.Analyzers/  # Roslyn custom analyzer (D003 machine enforcement)
├── scripts/                                   # gates + package-*.sh + release-preflight.sh + release-notes.sh
└── docs/                                      # architecture, testing, development, user guide, FAQ, cookbook
```

## Documentation

| Document | Contents |
|---|---|
| [User guide](docs/user-guide.en.md) | Install, first launch, daily use, data and log locations, troubleshooting |
| [FAQ](docs/faq.en.md) | Port drift, plugin market, data interop, MCP connections and other frequent questions |
| [Architecture](docs/architecture.md) | Startup model, supervision and failure surface, plugin assembly, self-update, packaging (single source of truth) |
| [Testing](docs/testing.md) | CI matrix, real-machine validation boundaries, gate list |
| [Development](docs/development.md) | Run steps, dev isolation, packaging and self-signing |
| [Cookbook](docs/cookbook.md) | Field-tested discriminators (upstream symptoms vs. local faults) |

## License & acknowledgements

- This project: MIT.
- [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) (MIT) — the runtime this shell hosts.
- [Avalonia](https://avaloniaui.net/) (MIT) — the host framework and native WebView control.
- Packaging approach references [pilot-harness](https://github.com/op7418/pilot-harness).

[![](https://img.shields.io/badge/powered_by-dsh-4D6BFE?style=flat-square&logo=deepseek&logoColor=white)](https://github.com/deepseek-ai/deepseek-harness)
