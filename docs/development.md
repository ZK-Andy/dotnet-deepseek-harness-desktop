# Development

> 本地开发与打包手册（`v0.1.12`）。

## 环境

* `.NET 10 SDK`（`global.json` 10.0.x，`TargetFramework net10.0`，`PublishAot true` 但 `publish -p:PublishAot=false --self-contained`）。
* `Linux: WebKitGTK 6`（`libwebkitgtk-6.0-4` / `libwebkitgtk-6.0.so.4`），`Node 22.23.1`（仅打包脚本下载），`pnpm 11`，`dpkg-deb/rpmbuild`（仅 `package-linux.sh` 全量，`CI` 提供）。
* 沙箱 `/home` 只读：`dotnet` 需 `DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget`。

## 目录与配置

```
src/DeepSeek.Harness.Desktop/  Program.cs, Services/*, Commands/*, ryn.json, wwwroot/
tests/DeepSeek.Harness.Desktop.Tests/  xunit 12
resources/runtime/  node + node_modules + dshmarket.tgz（gitignore）
scripts/  bundle-runtime{-ci,}.sh, package-linux.sh, verify-*.py
```

* `appsettings.json`：`DevTools:false`；`ryn.json`：`identifier` 与 `StartupWMClass` 同值 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`。
* 环境变量：`DSH_DESKTOP_RUNTIME_DIR`（覆盖 `resources/runtime`）、`DSH_DESKTOP_DSH_HOME`（覆盖 `~/.local/share/DeepSeek.Harness.Desktop/dsh`）、`DSH_DEVTOOLS=1`（`WebView` 调试）、`DEEPSEEK_API_KEY`（`dsh` 启动必需）。

## 捆绑运行时

```sh
# 统一入口（CI 同款，下载 Node + pnpm 闭包 + curl 497K tgz，~421M）
bash scripts/bundle-runtime-ci.sh linux-x64
# 兼容 wrapper
bash scripts/bundle-runtime.sh
```

* 入口校验：`resources/runtime/node` + `node_modules/@deepseek-ai/dsh/lib/bin.js` + `dshmarket.tgz 497K`；`package-linux.sh --stage-only` 在错布局/假包时 `fail loud`。

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
* 市场：首启 `3s` 后台 `dsh plugin add file:…tgz`，`v0.1.12` 装完 `host.Stop()→Supervisor` 重启即现；`pnpm-workspace.yaml` 的 `allowBuilds` 6 项由壳自愈。

## 测试与门禁

```sh
export DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget
dotnet build dotnet-deepseek-harness-desktop.slnx -c Release
dotnet test dotnet-deepseek-harness-desktop.slnx -c Release

python3 scripts/verify-adr-format.py
python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json
python3 scripts/verify-md-links.py
scripts/change-scope.sh origin/main HEAD
```

## 打包

```sh
# 仅校验布局（沙箱可用）
bash scripts/package-linux.sh --stage-only artifacts/publish-linux-x64
# 全量（需 dpkg-deb + rpmbuild，CI 走此路）
dotnet publish src/DeepSeek.Harness.Desktop -c Release -r linux-x64 -p:PublishAot=false --self-contained true -o artifacts/publish-linux-x64
VERSION=0.1.12 bash scripts/package-linux.sh artifacts/publish-linux-x64
# 产物：artifacts/linux-x64/*.deb + rpmbuild/RPMS/**/*.rpm + SHA256SUMS（tag 触发）
```

* `CI`：`ci.yml`（门禁+build+test）与 `package-linux.yml`（`concurrency` + `7 天 Artifacts` + `Release`）。

## 常见问题

* `/home` 只读：写 `DSH_HOME` 由壳注入 `~/.local/share/DeepSeek.Harness.Desktop/dsh/.pnpm-store`，用 `systemd-run --user` 清缓存：`systemd-run --user --pipe --wait bash -c 'rm -rf ~/.local/share/DeepSeek.Harness.Desktop/dsh'`
* `dsh web` 不出 `URL`：看 `host.StderrTail` 与 `journalctl --user -t deepseek-harness-desktop.desktop`；`60s` 超时仅看日志，不以 `timeout` 退出码判。
* `Wayland` 任务栏 `generic`：`ryn.json:identifier` 与 `desktop StartupWMClass` 必须同为 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`。
