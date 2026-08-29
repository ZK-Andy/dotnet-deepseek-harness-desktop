# Development

> 本地开发与打包手册。版本单一事实源 = `src/DeepSeek.Harness.Desktop/DeepSeek.Harness.Desktop.csproj` 的 `<Version>`。

## 环境

* `.NET 10 SDK`（`global.json` 10.0.x，`TargetFramework net10.0`，`PublishAot false`，发布即 JIT）
* `Linux: WebKitGTK 6`（`libwebkitgtk-6.0-4` / `libwebkitgtk-6.0.so.4`），`Node 24.20.0`（仅打包脚本下载），`pnpm 11`，`dpkg-deb/rpmbuild`（仅 `package-linux.sh` 全量，`CI` 提供）。
* 沙箱 `/home` 只读：`dotnet` 需 `DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget`。

## 目录与配置

```
src/DeepSeek.Harness.Desktop/  Program.cs, Services/*, Commands/*, ryn.json, wwwroot/
tests/DeepSeek.Harness.Desktop.Tests/  xunit（35 个测试文件，清单见 testing.md）
scripts/  build-companion-tgz.sh, package-*.sh, release-preflight.sh, verify-*.py
```

* `appsettings.json`：`DevTools:false`；`ryn.json`：`identifier` 与 `StartupWMClass` 同值 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`。
* 环境变量：`DSH_DESKTOP_RUNTIME_DIR`（覆盖捆绑运行时目录，dev 信号之一）、`DSH_DESKTOP_DEV=1`（显式 dev 声明——dev 判定只认这两个显式标记，不探测闭包存在性）、`DSH_DESKTOP_DSH_HOME`（桌面专属覆盖，默认共享 `~/.dsh`；dev 自动隔离到 `<仓库>/.cache/dev-home`）、`DSH_DEVTOOLS=1`（`WebView` 调试）、`DEEPSEEK_API_KEY`（`dsh` 启动必需）。

## 运行时来源（online-first）

安装器/仓库不再捆绑运行时闭包（ADR online-first-unbundled-runtime）：无捆绑闭包且无 PATH dsh 时，
首启引导下载钉版 Node（SHA256 校验）并经 npm 安装 dsh（落位 `~/.dsh-desktop/runtime`）。

## 运行与调试

```sh
export DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget
# PATH dsh（无 PATH dsh 时走首启引导，需网络）
dotnet run --project src/DeepSeek.Harness.Desktop
# 显式 dev 隔离（dev 判定改显式标记后必须带，防污染真实 home / 单实例互斥）
DSH_DESKTOP_DEV=1 dotnet run --project src/DeepSeek.Harness.Desktop
# 调试
DSH_DEVTOOLS=1 dotnet run --project src/DeepSeek.Harness.Desktop
```

* `HarnessRuntimeHost` 抓 `dsh web:` 日志；`RuntimeSupervisor` 崩溃自动重启（端口复用保 `origin`）。
* 无捆绑运行时且无 PATH dsh 时，首启经 `RuntimeBootstrap` 引导（复用/下载钉版 Node → npm 装 `@deepseek-ai/dsh@latest`），在 spawn dsh 前一次就位。
* 插件面均在 spawn dsh 前就位：companion（internal）静默自愈（`EnsureBundledPluginsBeforeSpawnAsync`，`BundledPluginCatalog` 清单单条 `plugin add`，版本感知升级），dshmarket（preset）经引导页「插件准备」步确认/跳过（`desktop.preinstall.choose` 决策）；`pnpm-workspace.yaml` 的 `allowBuilds` 6 项由壳自愈（companion 与市场用），不再「装后 `host.Stop()` 重启」。启动前 `DesktopProfileBootstrap.ReconcileProfile` 移除不可解析 bundle 引用。

## 测试与门禁

```sh
export DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget
dotnet build dotnet-deepseek-harness-desktop.slnx -c Release
dotnet test dotnet-deepseek-harness-desktop.slnx -c Release

python3 scripts/verify-adr-format.py
python3 scripts/verify-cookbook.py
python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json
python3 scripts/verify-md-links.py
python3 scripts/verify-handoff-structure.py
python3 scripts/verify-governance.py
scripts/change-scope.sh origin/main HEAD
```

## 打包

```sh
# 仅校验布局（沙箱可用）
bash scripts/package-linux.sh --stage-only artifacts/publish-linux-x64
ARCH=arm64 bash scripts/package-linux.sh --stage-only artifacts/publish-linux-arm64
bash scripts/package-macos.sh --stage-only artifacts/publish-osx-arm64
bash scripts/package-windows.sh --stage-only artifacts/publish-win-x64
# 全量（需 dpkg-deb/rpmbuild；mac 需 hdiutil，win 需 Inno Setup/NSIS —— CI 走此路）
dotnet publish src/DeepSeek.Harness.Desktop -c Release -r linux-x64 --self-contained true -o artifacts/publish-linux-x64
VERSION=<csproj 版本> bash scripts/package-linux.sh artifacts/publish-linux-x64
ARCH=arm64 VERSION=<csproj 版本> bash scripts/package-linux.sh artifacts/publish-linux-arm64
bash scripts/package-macos.sh artifacts/publish-osx-arm64  # + osx-x64
bash scripts/package-windows.sh artifacts/publish-win-x64
# 产物：artifacts/linux-{x64,arm64}/*.{deb,rpm}（rpm 已收敛顶层）, artifacts/osx-*/*.dmg, artifacts/win-x64/*.exe + SHA256SUMS.txt（tag 触发）
```

### 自签（仅内部/开发）

[ADR: implemented/process/2026-08-20-free-self-sign-dev.md](../.agents/notes/implemented/process/2026-08-20-free-self-sign-dev.md)

```sh
# macOS：ad-hoc 或指定身份
SELF_SIGN=1 bash scripts/package-macos.sh artifacts/publish-osx-arm64
SELF_SIGN=1 MACOS_SIGN_IDENTITY="Developer ID Application: Name (ID)" bash scripts/package-macos.sh artifacts/publish-osx-arm64
# Windows：signtool + CurrentUser\My 自签证书（缺则自动创建）
SELF_SIGN=1 bash scripts/package-windows.sh artifacts/publish-win-x64
# CI：workflow_dispatch 勾选 self_sign=true
```

* **边界**：自签/ad-hoc 仅消除**本机/内部**的"来源不明/未知发布者"告警，**不消除终端用户**的 Gatekeeper/SmartScreen——免费受信签名不存在，现状不签名、发布路径（tag 触发）不受影响。macOS 走 `codesign --force --deep --sign`（默认 `-` ad-hoc），Windows 走 `signtool sign /fd SHA256 /s My /n "DeepSeek Harness Desktop Dev"`。

* `CI`：`ci.yml`（门禁+build+test+coverage）+ `package-linux/macos/windows.yml`（出包 + `7 天 Artifacts`）+ 统一 `release.yml`（tag 触发，聚合产物并发布结构化的单个 Release，正文由 `scripts/release-notes.sh` 生成：`bash scripts/release-notes.sh [from] [to]`）。

## 常见问题

* `/home` 只读：写 `DSH_HOME` 由壳注入 `~/.dsh/.pnpm-store`，用 `systemd-run --user` 清缓存：`systemd-run --user --pipe --wait bash -c 'rm -rf ~/.dsh'`
* `dsh web` 不出 `URL`：看 `host.StderrTail` 与 `journalctl --user -t deepseek-harness-desktop.desktop`；`60s` 超时仅看日志，不以 `timeout` 退出码判。
* `Wayland` 任务栏 `generic`：`ryn.json:identifier` 与 `desktop StartupWMClass` 必须同为 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`。
