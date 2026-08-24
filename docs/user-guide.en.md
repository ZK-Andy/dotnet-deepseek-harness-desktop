# User Guide

> For users of DeepSeek Harness Desktop. Developer docs: [architecture.md](architecture.md) / [development.md](development.md). FAQ: [faq.en.md](faq.en.md).

## Requirements & Download

Grab the installer for your platform from [GitHub Releases](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases) (a `SHA256SUMS.txt` is attached to every release):

| Platform | Installer |
|---|---|
| Windows 10/11 x64 | `..._windows-x64-setup.exe` |
| macOS Apple Silicon / Intel | `..._macos-arm64.dmg` / `..._macos-x64.dmg` |
| Debian/Ubuntu x64 | `..._linux-amd64.deb` |
| Fedora/RHEL x86_64 | `..._linux-x86_64.rpm` |

> Linux arm64 is not provided for now (upstream native library unavailable; the package would not start). This table updates when it returns.

**No Node.js or command-line tools required** — the full DeepSeek Harness runtime is bundled; download and run.

## Install

- **Windows**: run the setup.exe and follow the wizard.
- **macOS**: open the DMG and drag the app into Applications.
- **Linux**: `sudo apt install ./....deb` or `sudo dnf install ./....rpm` (WebKitGTK dependencies resolved by the package manager).

Unsigned builds: this project is open source and ships without paid code signing. If macOS Gatekeeper blocks the first launch → right-click → Open, or allow it in System Settings → Privacy & Security; on Windows SmartScreen → More info → Run anyway. See [README](../README.en.md).

## First Launch

1. The shell starts the bundled runtime automatically and loads the UI.
2. Configure your own model API credentials in the in-app model settings.
3. On first run the bundled plugins (market, desktop companion) install silently in the background; a brief dark "reconnecting" screen while the runtime restarts is **normal** — a couple of seconds.
4. Once ready, browse the plugin market and install community plugins.

## Daily Use

- **Session restore**: restarting the app returns you to your last session; runtime crashes auto-recover into the current conversation.
- **External links** open in your system browser.
- **Desktop update entry**: the "Desktop Settings" section in Settings shows the current version and offers a manual check.
- **System tray**: a tray icon stays resident with a menu of Show window / Check for updates / Quit; clicking the window close button **hides to the tray** instead of quitting — reopen or quit from the tray menu.
- **Launch at login**: toggle it under Settings → "Desktop Settings" → "Launch at login".

## Updates

- New versions are checked once at startup; you can also check manually under Settings → "Desktop Settings", or via the tray menu's "Check for updates".
- When an update is found, one click runs the flow: authorize → app exits → install completes → relaunch on the new version.
- Packages are SHA256-verified; macOS guides you through a manual replace.
- Cancel at any time — the current version stays untouched.

## Data & Logs

The desktop shares **one data directory with the DeepSeek Harness ecosystem** — sessions, credentials and workspaces interoperate with CLI/TUI/Web; the desktop keeps plugin assembly in its own `profiles/desktop` subdirectory:

| Platform | Data directory |
|---|---|
| Linux / macOS | `~/.dsh` |
| Windows | `%USERPROFILE%\.dsh` |

Set the `DSH_HOME` environment variable to move everything elsewhere (same semantics as upstream tools); `DSH_DESKTOP_DSH_HOME` affects only the desktop.

Runtime logs are written to `logs/host.log` inside that directory.

**Upgrading from v0.2.x or earlier**: old versions used a private directory (Linux `~/.local/share/DeepSeek.Harness.Desktop/dsh`, macOS `~/Library/Application Support/DeepSeek.Harness.Desktop/dsh`, Windows `%LOCALAPPDATA%\DeepSeek.Harness.Desktop\dsh`). The new version does **not migrate automatically**: back up before upgrading; the new directory starts clean. Old data stays in place — copy the sessions/credentials you need into the new directory, or set `DSH_DESKTOP_DSH_HOME` to point back at the old one; delete it once you no longer need it.

**Full uninstall**: remove the app package, then delete the data directory above.

## Troubleshooting

1. **Blank window or endless "reconnecting"**: check the tail of `logs/host.log`.
2. **Window "disappeared" after closing**: the close button hides the app to the system tray (it keeps running); pick "Show window" from the tray menu to bring it back, or quit fully via the tray menu. On desktops without a tray, the app logs a fallback and closing exits for real.
3. **Update fails**: signature verification failures refuse to install (by design); re-download the latest installer and install over the old one.
4. **Plugin market missing**: a slow network can stall the first-run install; restarting the app usually self-heals.
5. **Collect diagnostics**: Settings → "Desktop Settings" → "Export diagnostics" creates a zip (logs and runtime state only; no sessions or credentials). If the window never opens, run the installed executable with `--export-diagnostics`.
6. Still stuck? Open an [issue](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/issues) with your platform and the relevant part of `host.log`.
