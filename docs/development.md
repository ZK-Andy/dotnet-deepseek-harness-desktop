# Development

> 本地开发与打包手册。版本单一事实源 = `src/DeepSeek.Harness.Desktop/DeepSeek.Harness.Desktop.csproj` 的 `<Version>`。

## 环境

* `.NET 10 SDK`（`global.json` 10.0.x，`TargetFramework net10.0`，`PublishAot false`，发布即 JIT）
* `Linux: WebKitGTK 6`（`libwebkitgtk-6.0-4` / `libwebkitgtk-6.0.so.4`），`Node 24.20.0`（仅打包脚本下载），`pnpm 11`，`dpkg-deb/rpmbuild`（仅 `package-linux.sh` 全量，`CI` 提供）。
* 沙箱 `/home` 只读：`dotnet` 需 `DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget`。

## 目录与配置

```
src/DeepSeek.Harness.Desktop/  Program.cs, DesktopBootstrap*, Commands/*, ryn.json, wwwroot/
src/DeepSeek.Harness.Desktop.Core/  纯逻辑（零外层引用）
src/DeepSeek.Harness.Desktop.Infrastructure/  适配器（Update/Runtime/CliShim/Plugins/Platform/*）
tests/DeepSeek.Harness.Desktop{.Core,.Infrastructure,}.Tests/  xunit（清单见 testing.md）
scripts/  build-companion-tgz.sh, package-*.sh, release-preflight.sh, verify-*.py, coverage-summary.py,
          smoke-*.sh（三平台安装冒烟）、dev-sign.sh（开发自签）、install-linters.sh（shellcheck/actionlint 钉版装）
scripts/lib/  common.sh, packaging-common.sh, smoke-wait-lib.sh, smoke-verdict-lib.sh, smoke-selftest.sh
packaging/windows/installer.iss.in  Inno 安装器模板（package-windows.sh 渲染占位后交 ISCC）
```

* `appsettings.json`：`DevTools:false`；`ryn.json`：`identifier` 与 `StartupWMClass` 同值 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`。
* 环境变量：`DSH_DESKTOP_RUNTIME_DIR`（dev 信号之一；全局 dsh 模型下不解析运行时目录）、`DSH_DESKTOP_DEV=1`（显式 dev 声明——dev 判定只认这两个显式标记，不探测闭包存在性）、`DSH_DESKTOP_DSH_HOME`（桌面专属覆盖，默认共享 `~/.dsh`；dev 自动隔离到 `<仓库>/.cache/dev-home`）、`DSH_DEVTOOLS=1`（`WebView` 调试）、`DEEPSEEK_API_KEY`（`dsh` 启动必需）。CLI shim 注册的测试隔离覆盖：`DSH_DESKTOP_CLI_BIN_DIR`（覆盖 shim 落盘目录）、`DSH_DESKTOP_CLI_RC_HOME`（覆盖 shell rc 基目录）——**仅在设置这两个变量时** shim 注册不写用户真实路径；未设置时（正常运行）shim 写用户真实路径（`%LOCALAPPDATA%\deepseek-harness\bin` / `~/.local/bin` + HKCU Path / shell rc），此即产品行为。

## 运行时来源与插件装配

运行时来源（系统全局 node + 全局 dsh、首启引导）与插件装配（spawn 前就位、CLI shim）的**单一事实源**在 [architecture.md](architecture.md)「运行时来源」「插件装配」两节；本文件只保留运行步骤与开发面配置。

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

* `HarnessRuntimeHost` 抓 `dsh web:` 日志；`Core.RuntimeSupervisor`（经 `IRuntimeHost` 端口）崩溃自动重启（端口复用保 `origin`）。
* 启动编排、回环代理与落定模型见 [architecture.md](architecture.md)「启动模型」；插件面均在 spawn dsh 前就位（见 [architecture.md](architecture.md)「插件装配」）。

## 测试与门禁

文档门禁全集（verify-* 各脚本与三档执行点）的**单一事实源**是 [testing.md](testing.md)「门禁」表（规则面在根 `AGENTS.md` 质量门节），此处不重复清单。本地开发最短路径：

```sh
export DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget
dotnet build dotnet-deepseek-harness-desktop.slnx -c Release
dotnet test dotnet-deepseek-harness-desktop.slnx -c Release
python3 scripts/verify-adr-format.py          # 其余 verify-* 按 testing.md 门禁表按需跑
scripts/change-scope.sh origin/main HEAD
```

脚本层静态检查与离线夹具（`shellcheck`/`actionlint` 由 `install-linters.sh` 钉版装到 `.cache/bin/`）：

```sh
bash scripts/install-linters.sh                     # shellcheck + actionlint（钉版本 + sha256 校验）
actionlint -shellcheck="shellcheck -S warning"      # 工作流静态检查（含 run: 块 shellcheck）
bash scripts/package-linux.sh --self-test           # 三包脚本共用头部 + 架构映射（离线）
bash scripts/verify-package-layout.sh --self-test   # 包布局三平台分支（离线假内容根）
bash scripts/smoke-install-linux.sh --self-test     # 冒烟判定面（共用夹具 + Linux 截图工具级联）
# macos/windows 冒烟同法；CI docs job 每次 push 跑全集
```

## 打包

```sh
# 仅校验布局（沙箱可用）
bash scripts/package-linux.sh --stage-only artifacts/publish-linux-x64
ARCH=arm64 bash scripts/package-linux.sh --stage-only artifacts/publish-linux-arm64
bash scripts/package-macos.sh --stage-only artifacts/publish-osx-arm64
bash scripts/package-windows.sh --stage-only artifacts/publish-win-x64
# 全量（需 dpkg-deb/rpmbuild；mac 需 hdiutil，win 需 Inno Setup（唯一链，缺 ISCC 即 fail loud）—— CI 走此路）
dotnet publish src/DeepSeek.Harness.Desktop -c Release -r linux-x64 --self-contained true -o artifacts/publish-linux-x64
VERSION=<csproj 版本> bash scripts/package-linux.sh artifacts/publish-linux-x64
ARCH=arm64 VERSION=<csproj 版本> bash scripts/package-linux.sh artifacts/publish-linux-arm64
bash scripts/package-macos.sh artifacts/publish-osx-arm64  # + osx-x64
bash scripts/package-windows.sh artifacts/publish-win-x64
# 产物：artifacts/linux-{x64,arm64}/*.{deb,rpm}（rpm 已收敛顶层）, artifacts/osx-*/*.dmg, artifacts/win-x64/*.exe + SHA256SUMS.txt（tag 触发）
```

### 自签（仅内部/开发）

[ADR: implemented/process/2026-08-20-free-self-sign-dev.md](../.agents/notes/implemented/process/2026-08-20-free-self-sign-dev.md)

签名实现唯一家在 `scripts/dev-sign.sh`（三包脚本只在 `SELF_SIGN=1` 时转调它）：

```sh
# 对已产出的产物直接签（不改包）
bash scripts/dev-sign.sh macos artifacts/osx-arm64/*.app
bash scripts/dev-sign.sh windows artifacts/win-x64/DeepSeek.Harness.Desktop_*_windows-x64-setup.exe
# macOS：ad-hoc 或指定身份
SELF_SIGN=1 bash scripts/package-macos.sh artifacts/publish-osx-arm64
SELF_SIGN=1 MACOS_SIGN_IDENTITY="Developer ID Application: Name (ID)" bash scripts/package-macos.sh artifacts/publish-osx-arm64
# Windows：signtool + CurrentUser\My 自签证书（缺则自动创建）
SELF_SIGN=1 bash scripts/package-windows.sh artifacts/publish-win-x64
# CI：workflow_dispatch 勾选 self_sign=true
```

* **边界**：自签/ad-hoc 仅消除**本机/内部**的"来源不明/未知发布者"告警，**不消除终端用户**的 Gatekeeper/SmartScreen——免费受信签名不存在，现状不签名、发布路径（tag 触发）不受影响。macOS 走 `codesign --force --deep --sign`（默认 `-` ad-hoc），Windows 走 `signtool sign /fd SHA256 /s My /n "DeepSeek Harness Desktop Dev"`。

* `CI`：`ci.yml`（门禁+build+test+coverage）+ `.github/workflows/package.yml`（三平台出包 + `7 天 Artifacts`；`workflow_dispatch` 单跑预览）+ 统一 `release.yml`（tag 触发；手动 dispatch 在分支 ref 上只出包不发布，调用前者并在同一 run 内聚合产物、发布结构化的单个 Release，正文由 `scripts/release-notes.sh` 生成：`bash scripts/release-notes.sh [from] [to]`）。

## 常见问题

* `/home` 只读：写 `DSH_HOME` 由壳注入 `~/.dsh/.pnpm-store`，用 `systemd-run --user` 清缓存：`systemd-run --user --pipe --wait bash -c 'rm -rf ~/.dsh'`
* `dsh web` 不出 `URL`：看 `host.StderrTail` 与 `journalctl --user -t deepseek-harness-desktop.desktop`；`60s` 超时仅看日志，不以 `timeout` 退出码判。
* `Wayland` 任务栏 `generic`：`ryn.json:identifier` 与 `desktop StartupWMClass` 必须同为 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`。
