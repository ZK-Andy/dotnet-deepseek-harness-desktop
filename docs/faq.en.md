# FAQ

Quick answers for download and daily-use questions. See [user-guide.en.md](user-guide.en.md) for full steps.

**Q: Windows SmartScreen warns about an unknown publisher?**
Expected — the project ships without paid code signing. Choose "More info" → "Run anyway"; macOS Gatekeeper works the same way (right-click → Open, or allow in System Settings).

**Q: Why is the installer so large?**
It bundles the complete DeepSeek Harness runtime (Node + dependency closure) for a zero-environment, works-offline experience. No Node.js needed and your system PATH stays untouched.

**Q: A dark "reconnecting" screen on first launch — is that normal?**
Yes. First launch installs bundled plugins in the background and restarts the runtime; it takes a couple of seconds.

**Q: Are my sessions preserved after an upgrade?**
Yes within the same data directory (v0.3+ upgrades). Sessions persist locally and the app keeps a stable port, so it returns to your last session automatically.

**Q: Where does my data live? Is it uploaded?**
Everything stays in the shared data directory `~/.dsh` on your machine (`%USERPROFILE%\.dsh` on Windows; paths in [user-guide.en.md](user-guide.en.md)). Model calls go straight from your machine to the provider you configured.

**Q: Does it share data with the dsh CLI / TUI?**
Yes. The desktop uses the same canonical home as CLI/TUI/Web (`~/.dsh`, overridable via `DSH_HOME`), so sessions, credentials and workspaces interoperate; desktop plugin assembly lives in a dedicated `profiles/desktop` subdirectory. Upgrading from v0.2.x or earlier is a breaking switch — the old private directory is not migrated automatically; back up first, see [user-guide.en.md](user-guide.en.md).

**Q: System tray / minimize to tray / autostart?**
All included: a resident tray icon (menu: Show window / Check for updates / Quit), close hides to the tray by default, and launch-at-login toggles under Settings → "Desktop Settings". See [user-guide.en.md](user-guide.en.md).

**Q: Update failed or the update button shows an error?**
Packages are SHA256-verified; failed verification refuses to install (by design). Re-download the latest installer from Releases and install over — user data is untouched.

**Q: Missing Linux dependencies?**
The deb/rpm declare their runtime dependencies (WebKitGTK etc.); install through your package manager rather than extracting manually.

**Q: How do I report bugs or contribute?**
Use the [issue templates](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/issues/new/choose); PRs follow the repository checklist.
