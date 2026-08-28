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
resources/runtime/  node + node_modules + dshmarket.tgz（gitignore）
scripts/  bundle-runtime{-ci,}.sh, package-*.sh, check-pin-freshness.sh, release-preflight.sh, verify-*.py
```

* `appsettings.json`：`DevTools:false`；`ryn.json`：`identifier` 与 `StartupWMClass` 同值 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`。
* 环境变量：`DSH_DESKTOP_RUNTIME_DIR`（覆盖 `resources/runtime`）、`DSH_DESKTOP_DSH_HOME`（桌面专属覆盖，默认共享 `~/.dsh`；dev 自动隔离到 `<仓库>/.cache/dev-home`）、`DSH_DEVTOOLS=1`（`WebView` 调试）、`DEEPSEEK_API_KEY`（`dsh` 启动必需）。

## 捆绑运行时

```sh
# 统一入口（CI 同款，下载 Node + pnpm 闭包 + curl 497K tgz，~421M）
bash scripts/bundle-runtime-ci.sh linux-x64
# 兼容 wrapper
bash scripts/bundle-runtime.sh
```

* 入口校验：`resources/runtime/node` + `node_modules/@deepseek-ai/dsh/lib/bin.js`；闭包签名 `.bundle-meta.json` 含 dsh/node/companion/market/trimPolicy/scriptSha256 六维，`restore-keys` 前缀命中捡回的旧闭包校验不过即全量重建。`package-linux.sh --stage-only` 在错布局/假包时 `fail loud`。

## 运行与调试

```sh
export DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget
# PATH dsh
dotnet run --project src/DeepSeek.Harness.Desktop
# 内置运行时
DSH_DESKTOP_RUNTIME_DIR=$PWD/resources/runtime dotnet run --project src/DeepSeek.Harness.Desktop
# 调试
DSH_DEVTOOLS=1 dotnet run --project src/DeepSeek.Harness.Desktop
```

* `HarnessRuntimeHost` 抓 `dsh web:` 日志；`RuntimeSupervisor` 崩溃自动重启（端口复用保 `origin`）。
* 随包插件：首启 `3s` 后台按 `BundledPluginCatalog` 清单单条 `dsh plugin add <spec…>` 装齐 `dshmarket + dsh-desktop-companion`（版本感知升级），装完 `host.Stop()→Supervisor` 重启即现；`pnpm-workspace.yaml` 的 `allowBuilds` 6 项由壳自愈。

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

* `CI`：`ci.yml`（门禁+build+test+coverage）+ `package-linux/macos/windows.yml`（出包 + `7 天 Artifacts`）+ 统一 `release.yml`（tag 触发，聚合产物并发布结构化的单个 Release，正文由 `scripts/release-notes.sh` 生成：`bash scripts/release-notes.sh [from] [to]`）+ `freshness.yml`（每周钉版巡检：npm dist-tags ×2 + Node 现役 LTS 对三处钉版，漂移开/更新 issue、追平自动关；`bash scripts/check-pin-freshness.sh --self-test` 离线自测，发版 preflight 附 warn-only 漂移注解）。

## 常见问题

* `/home` 只读：写 `DSH_HOME` 由壳注入 `~/.dsh/.pnpm-store`，用 `systemd-run --user` 清缓存：`systemd-run --user --pipe --wait bash -c 'rm -rf ~/.dsh'`
* `dsh web` 不出 `URL`：看 `host.StderrTail` 与 `journalctl --user -t deepseek-harness-desktop.desktop`；`60s` 超时仅看日志，不以 `timeout` 退出码判。
* `Wayland` 任务栏 `generic`：`ryn.json:identifier` 与 `desktop StartupWMClass` 必须同为 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`。
