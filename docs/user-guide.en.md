# User Guide

> For users of DeepSeek Harness Desktop. Developer docs: [architecture.md](architecture.md) / [development.md](development.md). FAQ: [faq.en.md](faq.en.md).

## Requirements & Download

Grab the installer for your platform from [GitHub Releases](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases) (a `SHA256SUMS.txt` is attached to every release):

| Platform | Installer |
|---|---|
| Windows 10/11 x64 | `..._windows-x64-setup.exe` |
| macOS Apple Silicon / Intel | `..._macos-arm64.dmg` / `..._macos-x64.dmg` |
| Debian/Ubuntu x64 / arm64 | `..._linux-amd64.deb` / `..._linux-arm64.deb` |
| Fedora/RHEL x86_64 / aarch64 | `..._linux-x86_64.rpm` / `..._linux-aarch64.rpm` |

**No separate Node.js or DeepSeek Harness installation required** — the shell downloads and prepares the runtime on **first launch** (pinned Node download, dsh install from the registry). This needs **network**; progress is visible and failures are retryable.

## Install

- **Windows**: run the setup.exe and follow the wizard.
- **macOS**: open the DMG and drag the app into Applications.
- **Linux**: `sudo apt install ./....deb` or `sudo dnf install ./....rpm` (WebKitGTK dependencies resolved by the package manager).

Unsigned builds: this project is open source and ships without paid code signing. If macOS Gatekeeper blocks the first launch → right-click → Open, or allow it in System Settings → Privacy & Security; on Windows SmartScreen → More info → Run anyway. See [README](../README.en.md).

## First Launch

1. The shell auto-prepares and starts the DeepSeek Harness runtime, then loads the UI. On the first launch (no bundled runtime and no PATH dsh) it bootstraps: detects/reuses a local Node, otherwise downloads the pinned Node and installs dsh — progress visible throughout; **retry the progress page on offline/failure** (per-step timeout).
2. Configure your own model API credentials in the in-app model settings.
3. The bundled desktop companion plugin is required by the shell: it self-heals silently before dsh starts, then the UI opens.
4. The plugin market (`dshmarket`) is not installed by the shell; add it to the desktop profile with dsh's plugin command (`dsh plugin --profile dotnet-desktop add dshmarket@latest`), then browse and install community plugins.

## Daily Use

- **Session restore**: restarting the app returns you to your last session; runtime crashes auto-recover into the current conversation — if the preferred port is taken and it drifts, the origin changes (the page's selection has to be picked again, and the Update, Launch at sign-in, Minimize to tray on close and Export diagnostics controls in Settings may stop working until you restart the app).
- **External links** open in your system browser.
- **Desktop update entry**: the "Desktop Settings" section in Settings shows the current version and offers a manual check.
- **System tray**: a tray icon stays resident with a menu of Show window / Check for updates (when the self-update stack is available) / Restart / Quit; clicking the window close button **hides to the tray** instead of quitting — reopen, restart or quit from the tray menu.
- **Launch at login**: toggle it under Settings → "Desktop Settings" → "Launch at login".
- **Restart app**: Settings → "Desktop Settings" → "Restart app" quits and relaunches the desktop shell (session data is unaffected); on desktops without a system tray this is the only in-page restart entry, and when the command channel is down use the tray menu's "Restart" instead.

## Updates

- New versions are checked once at startup; you can also check manually under Settings → "Desktop Settings", or via the tray menu's "Check for updates" (when the self-update stack is available).
- When an update is found, one click runs the flow: authorize → app exits → install completes → relaunch on the new version.
- Packages are SHA256-verified; macOS guides you through a manual replace.
- Cancel at any time — the current version stays untouched.

## Data & Logs

The desktop shares **one data directory with the DeepSeek Harness ecosystem** — sessions, credentials and workspaces interoperate with CLI/TUI/Web; the desktop keeps plugin assembly in its own `profiles/dotnet-desktop` subdirectory (when upgrading from an earlier version, the old `profiles/desktop` subdirectory is renamed automatically on first launch — no manual step):

| Platform | Data directory |
|---|---|
| Linux / macOS | `~/.dsh` |
| Windows | `%USERPROFILE%\.dsh` |

Set the `DSH_HOME` environment variable to move everything elsewhere (same semantics as upstream tools); `DSH_DESKTOP_DSH_HOME` affects only the desktop.

Runtime logs are written to `logs/host.log` inside that directory.

**Upgrading from v0.2.x or earlier**: old versions used a private directory (Linux `~/.local/share/DeepSeek.Harness.Desktop/dsh`, macOS `~/Library/Application Support/DeepSeek.Harness.Desktop/dsh`, Windows `%LOCALAPPDATA%\DeepSeek.Harness.Desktop\dsh`). The new version does **not migrate automatically**: back up before upgrading; the new directory starts clean. Old data stays in place — copy the sessions/credentials you need into the new directory, or set `DSH_DESKTOP_DSH_HOME` to point back at the old one; delete it once you no longer need it.

**Full uninstall**: remove the app package, then delete the data directory above.

## Troubleshooting

1. **First launch stuck on download/install** (offline or slow mirror): the progress page shows the failing step and is retryable — confirm network and retry (per-step timeout). **Blank window or endless "reconnecting"**: check the tail of `logs/host.log`.
2. **Window "disappeared" after closing**: the close button hides the app to the system tray (it keeps running); pick "Show window" from the tray menu to bring it back, or quit fully via the tray menu. On desktops without a tray, the app logs a fallback and closing exits for real.
3. **Update fails**: signature verification failures refuse to install (by design); re-download the latest installer and install over the old one.
4. **Plugin market missing**: the shell does not install the market for you — add it to the desktop profile with dsh's plugin command (`dsh plugin --profile dotnet-desktop add dshmarket@latest`).
5. **Collect diagnostics**: Settings → "Desktop Settings" → "Export diagnostics" creates a zip (logs and runtime state only; no sessions or credentials). If the window never opens, run the installed executable with `--export-diagnostics`.
6. Still stuck? Open an [issue](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/issues) with your platform and the relevant part of `host.log`.
