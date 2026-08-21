# Architecture

> `v0.1.12` 现状。`Ryn` 壳 + 捆绑 `dsh` 运行时 + 崩溃监督 + 市场后台安装 + `pilot-harness` 打包。

## 概览

```
┌─────────────┐  spawn --profile web --port 0   ┌─────────────────┐
│ Ryn Shell   │ ─────────────────────────────▶ │ dsh web (Node)  │
│ (C#/.NET)   │  ◀─ dsh web: http://127.0.0.1 ─ │ @deepseek-ai/dsh│
│ WebView     │  opts.Url = webUrl            │ private DSH_HOME│
└─────────────┘                               └─────────────────┘
```

* 壳只管生命周期、窗口、恢复；`dsh` 的插件树即应用运行时。
* `DSH_HOME` 私有化：`~/.local/share/DeepSeek.Harness.Desktop/dsh`（`LocalApplicationData`），经 `HarnessRuntimeHost.ResolveDshHome()` 解析，环境变量 `DSH_DESKTOP_DSH_HOME` 覆盖。
* 无内置运行时回退 `PATH dsh`（开发期）。

## 壳与窗口

* `src/DeepSeek.Harness.Desktop/Program.cs`：`HarnessRuntimeHost.StartAsync(60s)` → `dsh web:` → `RynApplication.CreateBuilder().ConfigureOptions(opts.Url = webUrl)`。`ryn.json:identifier=io.github.ZK-Andy.dotnet-deepseek-harness-desktop` 与 `StartupWMClass` 同值，`Wayland/X11` 任务栏正确关联；`icon.png` 进 `AppContext.BaseDirectory` 并上 `hicolor/pixmaps`。
* `Services/CurrentWindowAccessor` 供 `RuntimeSupervisor` 与后台市场任务做 `EvaluateJavaScriptAsync`/`NavigateAsync`。

## 运行时定位与启动

* `Services/RuntimeLocator`：`ResolveRuntimeDirectory()` 优先 `DSH_DESKTOP_RUNTIME_DIR` 否则 `AppContext.BaseDirectory/resources/runtime`；`TryLocateBundled` 判 `node` + `node_modules/@deepseek-ai/dsh/lib/bin.js`（`pilot-harness` 整树入口）。
* `Services/HarnessRuntimeHost`：`ProcessStartInfo` 设 `DSH_HOME`、`pnpm_config_store_dir/cache_dir`（`DSH_HOME/.pnpm-store`）、`WorkingDirectory=AppContext.BaseDirectory`；`OutputDataReceived` 抓 `dsh web:` 的 `HarnessUrlParser`；`ErrorDataReceived` 留 `StderrTail` 8 行。`port 0` 首次 OS 分配并记忆，重启复用同端口保 `origin`，占位回退 `0`。
* `Services/HarnessUrlParser`：单行解析 `dsh web: http://127.0.0.1:<port>`。

## 崩溃监督

* `Services/RuntimeSupervisor`：`WaitForExitAsync` + `CancellationToken` 循环；退出→`showRecovery`（`RecoveryScript` 覆写文档为“重连中”）→`host.RestartAsync(60s)`→`navigate(newUrl)`。仅重启子进程，不重启桌面进程；`supervisorCts` 随 `app.Run()` 结束取消。

## 市场后台安装

* `Program.cs` 后台任务（`Task.Delay 3s`，不阻塞首启窗口）：
  1. `System.Text.Json` 判 `dependencies.dshmarket && bundles.contains(dshmarket)` 已装则跳过；清理 `0.1.10` 残留 `dependencies.app=file:...dshmarket.tgz`。
  2. `EnsureWorkspaceAllowBuilds` 把 `pnpm-workspace.yaml` 的 `allowBuilds` 6 项（`@deepseek-ai/dsh-subprocess-local/@google/genai/koffi/node-pty/protobufjs/esbuild`）置 `true`。
  3. `ResolveMarketSpec` 优先 `resources/runtime/dshmarket.tgz`（`>10K`）否则 `node_modules/dshmarket` 目录或 `dshmarket@1.15.0`。
  4.  spawn `bundled node dsh/lib/bin.js plugin --profile web add <spec>`（注入 `DSH_HOME/.pnpm-store`），`exit 0` 后 `EnsureBundlesContainsMarketAsync` 兜底。
  5. `EvaluateRecovery + host.Stop()` 交 `RuntimeSupervisor` 重启并导航新 `URL`，市场即现。
* `dsh` 的 `reconcilePlugins` 在 `plugin add` 后自动把 `dshmarket` 追加到 `dsh.profile.bundles`（`pilot-harness` 同款）。

## 打包

* `scripts/bundle-runtime-ci.sh`：下载 `Node 22.23.1`（`linux-x64/arm64`, `win-x64`, `osx-x64/arm64`）+ `pnpm add @deepseek-ai/dsh@${DSH_VERSION:-0.1.1-rc.1} --allow-build=*` + `dshmarket@1.15.0 --allow-build=esbuild`（`--store-dir $PNPM_STORE_DIR`，默认 `$HOME/.dsh-pnpm/store`，CI 由 `actions/cache` 跨 run 持久化缓存，命中免重下包/重编原生模块），`curl` 官方 `497K` `dshmarket.tgz`（`>10K/name` 双校验）→ `cp -a node_modules/. resources/runtime/node_modules/`，`60s` 抓 `dsh web:` 自检。
* `scripts/package-linux.sh`：`dotnet publish -r linux-(x64|arm64)` → `staging` 校验 `node + dsh/lib/bin.js + dshmarket.tgz 497K` → `deb (Depends: libwebkitgtk-6.0-4, arch amd64/arm64)` / `rpm (AutoReqProv:no, Requires: libwebkitgtk-6.0.so.4, BuildArch x86_64/aarch64)`。
* `scripts/package-macos.sh` / `package-windows.sh`：`dotnet publish -r osx-(x64|arm64)/win-x64` → `staging` 校验 → 单一安装产物：mac `dmg`（`hdiutil`，含 `.app`）/ win `exe` 安装器（`Inno Setup`/`NSIS`/`7z SFX`，`…-setup.exe`），文件名含 `…_macos-*/…_windows-*` 标识，签名占位（`codesign`/`signtool` 待证书）。**不单独产出便携 zip**（避免对 ~1.5GB 闭包重复压缩，对齐 pilot-harness 每平台单产物的思路）。
* `resources/runtime` 含整树 `node_modules` + `node(.exe)` + `dshmarket.tgz`，随 `usr/lib`/`Contents/Resources`/`stage` 进包；`CI`：`package-linux/macos/windows.yml` 只出包 + 上传 `7 天 Artifacts`；统 **`release.yml`**（`tag v*`）聚合三平台产物 → 合并 `SHA256SUMS` → 用 `scripts/release-notes.sh` 生成结构化正文，幂等创建单个 `Release`（单一 owner，不再并行重复）。

## 配置与扩展

* `appsettings.json`：`DevTools:false`（`DSH_DEVTOOLS=1` 开启）。
* `ryn.json`：`identifier/capabilities`。
* 扩展点：`DSH_DESKTOP_RUNTIME_DIR` / `DSH_DESKTOP_DSH_HOME` 覆盖。
