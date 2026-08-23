# FAQ

Quick answers for download and daily-use questions. See [user-guide.en.md](user-guide.en.md) for full steps.

**Q: Windows SmartScreen warns about an unknown publisher?**
Expected — the project ships without paid code signing. Choose "More info" → "Run anyway"; macOS Gatekeeper works the same way (right-click → Open, or allow in System Settings).

**Q: Why is the installer so large?**
It bundles the complete DeepSeek Harness runtime (Node + dependency closure) for a zero-environment, works-offline experience. No Node.js needed and your system PATH stays untouched.

**Q: A dark "reconnecting" screen on first launch — is that normal?**
Yes. First launch installs bundled plugins in the background and restarts the runtime; it takes a couple of seconds.

**Q: Are my sessions preserved after an upgrade?**
Yes. Sessions persist in the local data directory and the app keeps a stable port, so it returns to your last session automatically.

**Q: Where does my data live? Is it uploaded?**
Everything stays in the private data directory on your machine (paths in [user-guide.en.md](user-guide.en.md)). Model calls go straight from your machine to the provider you configured.

**Q: Does it share data with the dsh CLI / TUI?**
The current preview uses a separate private data directory. Sharing the canonical home (`~/.dsh`) with the CLI is on the roadmap as a breaking change; the README will call for backups when that ships.

**Q: System tray / minimize to tray / autostart?**
The tray is on the roadmap (see the todo list); other desktop basics will follow in subsequent releases.

**Q: Update failed or the update button shows an error?**
Packages are SHA256-verified; failed verification refuses to install (by design). Re-download the latest installer from Releases and install over — user data is untouched.

**Q: Missing Linux dependencies?**
The deb/rpm declare their runtime dependencies (WebKitGTK etc.); install through your package manager rather than extracting manually.

**Q: How do I report bugs or contribute?**
Use the [issue templates](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/issues/new/choose); PRs follow the repository checklist.
