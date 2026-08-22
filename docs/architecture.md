# Architecture

> 现状。`Ryn` 壳 + 捆绑 `dsh` 运行时 + 崩溃监督 + 随包插件后台安装 + `pilot-harness` 打包。

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
* `Services/CurrentWindowAccessor` 供 `RuntimeSupervisor` 与后台随包插件任务做 `EvaluateJavaScriptAsync`/`NavigateAsync`。

## 运行时定位与启动

* `Services/RuntimeLocator`：`ResolveRuntimeDirectory()` 优先 `DSH_DESKTOP_RUNTIME_DIR` 否则 `AppContext.BaseDirectory/resources/runtime`；`TryLocateBundled` 判 `node` + `node_modules/@deepseek-ai/dsh/lib/bin.js`（`pilot-harness` 整树入口）。
* `Services/HarnessRuntimeHost`：`ProcessStartInfo` 设 `DSH_HOME`、`pnpm_config_store_dir/cache_dir`（`DSH_HOME/.pnpm-store`）、`WorkingDirectory=AppContext.BaseDirectory`；`OutputDataReceived` 抓 `dsh web:` 的 `HarnessUrlParser`；`ErrorDataReceived` 留 `StderrTail` 8 行。`port 0` 首次 OS 分配并记忆，重启复用同端口保 `origin`，占位回退 `0`。
* `Services/HarnessUrlParser`：单行解析 `dsh web: http://127.0.0.1:<port>`。

## 崩溃监督

* `Services/RuntimeSupervisor`：`WaitForExitAsync` + `CancellationToken` 循环；退出→`showRecovery`（`RecoveryScript` 覆写文档为“重连中”）→`host.RestartAsync(60s)`→`navigate(newUrl)`。仅重启子进程，不重启桌面进程；`supervisorCts` 随 `app.Run()` 结束取消。

## 随包插件后台安装

* 随包插件两项：`dshmarket`（市场，registry 有上游）与 `dsh-desktop-companion`（桌面伴生：外部链接接管等壳集成，仅随包分发）。
* `Program.cs` 后台任务（`Task.Delay 3s`，不阻塞首启窗口）：
  0. 检测到 `DSH_DESKTOP_RUNTIME_DIR`（开发运行时覆盖，打包产品永不设置）即整体跳过——开发运行的默认 `DSH_HOME` 与已装正式版共享，防止把工作区 `file:` 依赖写进共享 profile。
  1. `MarketInstallHelper.IsBundleInstalled(pkg)` 精确判每项是否已就位（`dependencies.<pkg>` + `bundles` 含 `<pkg>`），收集未就位清单；清理 `0.1.10` 残留 `dependencies.app=file:...dshmarket.tgz`。
  1b. `dsh-desktop-companion` 已就位时做版本感知升级：`PluginVersionCheck` 比对随包 tgz 内 `package/package.json` 的 version 与 profile `node_modules` 副本 version，随包更新即入待装清单走同一安装管线（改插件必须 bump version，否则不触发；见 ADR `implemented/feature/2026-08-22-companion-plugin-version-aware-upgrade`）。
  2. `EnsureWorkspaceAllowBuilds` 把 `pnpm-workspace.yaml` 的 `allowBuilds` 6 项（`@deepseek-ai/dsh-subprocess-local/@google/genai/koffi/node-pty/protobufjs/esbuild`）置 `true`。
  3. spec 解析：市场走 `ResolveMarketSpec`（`resources/runtime/dshmarket.tgz >10K` → 目录 → `dshmarket@1.15.0`）；伴生走 `ResolveCompanionSpec`（tgz `>1K` → 闭包目录 → 无即跳过，无 registry 回退）。
  4. 单次 spawn `bundled node dsh/lib/bin.js plugin --profile web add <spec…>` 多包安装（注入 `DSH_HOME/.pnpm-store`），`exit 0` 后对每项 `EnsureBundlesContainsAsync(pkg)` 兜底。
  5. `EvaluateRecovery + host.Stop()` 交 `RuntimeSupervisor` 重启并导航新 `URL`。
* `dsh` 的 `reconcilePlugins` 在 `plugin add` 后自动把包名追加到 `dsh.profile.bundles`（`pilot-harness` 同款）。

## 外部链接接管

* `plugins/dsh-desktop-companion` 客户端半在 dsh web boot 时注册 capture 阶段点击监听：顶层帧的 http(s) 且 `target="_blank"` 或跨源链接 → `preventDefault` → `window.__ryn.invoke('app.openExternal', {url})`；同源与非 http(s) 放行。监听随每次页面加载重建，SPA 重渲染天然存活。与仍带旧注入脚本的已发布壳共存：双旗认领（`__ryn_externalLinkCatcher` / `__dshDesktopCompanionLinks`）保证每文档恰好一个处理器，待无在发版本携带注入脚本后移除。
* 宿主侧 `Services/ExternalLinkCommandRouter`（`ICommandRouter`）收命令，经 `ExternalLinkPolicy.IsExternalHttpLink` 复核后 `Process.Start(UseShellExecute)` 开系统浏览器。

## 自更新

* 状态机 `Services/Update/UpdateStateMachine`（移植 opencode updater-controller）：`idle→checking→downloading→ready→installing` + up-to-date/error；检查/下载/安装委托注入 + ready 持久化接口，纯逻辑可单测。启动对账（记录版本不高于当前或损坏 → 清记录）后自动检查一次，失败静默转 error；并发检查互斥。
* Feed：`releases.atom` 最新稳定 tag + `expanded_assets/<tag>` 抓资产 href（绕 api 限流）；`ReleaseMeta.Pick` 按 RID 后缀挑资产。下载 `.part` 原子改名 + SHA256SUMS 强校验（**release 未附校验文件或 HTTP 非 2xx 时 fail loud 拒装**）→ `<DSH_HOME>/updates/`。
* 安装：Linux pkexec 脚本（等本进程退出→dpkg/rpm→runuser 降权拉起新版）；Windows Inno `/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`；macOS v1 报错引导手动。
* UI：伴生插件注册 `sidebar.footer.action`（侧栏底部设置入口上方动作行）；**仅 ready 渲染**圆形下载钮，hover 展开版本文字，点击即装+重启；installing 转圈禁点。伴生插件另注册 `settings.section`「桌面更新」页（order 50，市场之后）：当前版本 + 手动检查按钮 + 完整状态行（error 显宿主传回原因；无自更新栈的运行时降级为页内不可用提示）。状态经宿主 CustomEvent `dsh-desktop-update` 推送，初值走 `ryn.invoke('desktop.update.getState')`；状态帧含 `current`（当前版本）与 error 态 `message` 字段。
* 参数：appsettings.json `Update` 节（Repository/超时/目录）；当前版本 = csproj `<Version>`（发布 CI 以 `-p:Version=` 覆盖，输入留空回退 csproj、空值 fail loud）。**dev 运行时不装载自更新栈**（除非 `DSH_DESKTOP_UPDATE_FORCE=1` 显式开启）。

## 打包

* `scripts/bundle-runtime-ci.sh`：下载 `Node 22.23.1`（`linux-x64/arm64`, `win-x64`, `osx-x64/arm64`）+ `pnpm add @deepseek-ai/dsh@${DSH_VERSION:-0.1.1-rc.2} --allow-build=*` + `dshmarket@1.15.0 --allow-build=esbuild`（`--store-dir $PNPM_STORE_DIR`，默认 `$HOME/.dsh-pnpm/store`，CI 由 `actions/cache` 跨 run 持久化缓存，命中免重下包/重编原生模块），`curl` 官方 `497K` `dshmarket.tgz`（`>10K/name` 双校验）+ 仓库源码 staging tar 出 `dsh-desktop-companion.tgz`（package/ 前缀，源码缺失 fail loud）→ `cp -a node_modules/. resources/runtime/node_modules/`，`60s` 抓 `dsh web:` 自检。
* `scripts/package-linux.sh`：`dotnet publish -r linux-(x64|arm64)` → `staging` 校验 `node + dsh/lib/bin.js + dshmarket.tgz 497K` → `deb (Depends: libwebkitgtk-6.0-4, arch amd64/arm64)` / `rpm (AutoReqProv:no, Requires: libwebkitgtk-6.0.so.4, BuildArch x86_64/aarch64)`。
* `scripts/package-macos.sh` / `package-windows.sh`：`dotnet publish -r osx-(x64|arm64)/win-x64` → `staging` 校验 → 单一安装产物：mac `dmg`（`hdiutil`，含 `.app`）/ win `exe` 安装器（`Inno Setup`/`NSIS`/`7z SFX`，`…-setup.exe`），文件名含 `…_macos-*/…_windows-*` 标识，签名占位（`codesign`/`signtool` 待证书）。**不单独产出便携 zip**（避免对 ~1.5GB 闭包重复压缩，对齐 pilot-harness 每平台单产物的思路）。
* `resources/runtime` 含整树 `node_modules` + `node(.exe)` + `dshmarket.tgz` + `dsh-desktop-companion.tgz`，随 `usr/lib`/`Contents/Resources`/`stage` 进包；`CI`：`package-linux/macos/windows.yml` 只出包 + 上传 `7 天 Artifacts`；统 **`release.yml`**（`tag v*`）聚合三平台产物 → 合并 `SHA256SUMS` → 用 `scripts/release-notes.sh` 生成结构化正文，幂等创建单个 `Release`（单一 owner，不再并行重复）。

## 配置与扩展

* `appsettings.json`：`DevTools:false`（`DSH_DEVTOOLS=1` 开启）；`Update` 节（自更新仓库/超时/目录）。
* `ryn.json`：`identifier/capabilities`。
* 扩展点：`DSH_DESKTOP_RUNTIME_DIR` / `DSH_DESKTOP_DSH_HOME` / `DSH_DESKTOP_UPDATE_FORCE`（dev 下显式开启自更新）覆盖。
* **开发运行时隔离**：设置 `DSH_DESKTOP_RUNTIME_DIR` 即进入 dev 模式——ApplicationId 自动加 `.dev` 后缀（与已装正式版可同时开窗，避开 GTK 同 id 单实例互斥），DSH_HOME 未显式覆盖时自动指向 `<仓库>/.cache/dev-home`；显式指回真实 home 时随包插件安装自动跳过防串扰。
