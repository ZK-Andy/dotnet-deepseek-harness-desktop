# Architecture

> 现状。`Ryn` 壳 + 依赖全局 dsh 的简单壳（ADR `implemented/architecture/2026-08-31-simple-shell-single-global-dsh`）+ 崩溃监督 + 首启引导装 dshmarket + 随包 companion 装配 + 安装器瘦身打包。

## 概览

```
┌─────────────┐ spawn --profile desktop --port 0 ┌─────────────────┐
│ Ryn Shell   │ ─────────────────────────────▶  │ dsh web (Node)  │
│ (C#/.NET)   │  ◀─ dsh web: http://127.0.0.1 ─ │ @deepseek-ai/dsh│
│ WebView     │  opts.Url = webUrl             │ shared ~/.dsh   │
└─────────────┘                                └─────────────────┘
```

* 壳只管生命周期、窗口、恢复；`dsh` 的插件树即应用运行时。
* **共享 home（B 形态）**：默认上游规范 `~/.dsh`，经 `HarnessRuntimeHost.ResolveDshHome()` 解析——优先级：`DSH_DESKTOP_DSH_HOME`（dev 隔离/用户回退）> 生态标准 `DSH_HOME` > `~/.dsh`；home 层数据（sessions/credentials/workspaces）与 CLI/TUI/Web 互通。桌面插件装配走专属 `profiles/desktop`（`DesktopProfileBootstrap` 在首次 spawn 前按上游 `initProfile` 同款三件套自举，bundles 对齐 web 模板）。
* 运行时 = 全机唯一一份全局 dsh（用户 PATH，`@deepseek-ai/dsh@alpha`）；桌面不运输行时闭包、不自备运行时目录（见「运行时定位与启动」）。
* **可观测性**（ADR `2026-08-24-shell-observability-diagnostics`）：全部壳侧诊断经 `HostLog` 双写 stdout 与 `<home>/logs/host.log`（超 5MB 滚动 .old）；supervisor 恢复时落子进程 stderr 尾部、自更新状态机每次变化留痕；`RunMarker` 启动占位/owner 清理判定非受控退出（横幅提示）；`desktop.diagnostics.export` + CLI `--export-diagnostics` 导出白名单诊断 zip 到用户文档目录。
* 启动期告知（ADR `implemented/architecture/2026-08-23-shared-home-desktop-profile`）：`RuntimeVersionGate` 只读探测 dsh 版本低于底线仅横幅提示不阻断；检测到 v0.2.x 私有 home 残留则在 host.log 留痕（界面横幅已去除，见 ADR `implemented/bug-fix/2026-08-24-companion-settings-consolidation`）。
* **系统托盘与 hide-to-tray**（ADR `implemented/architecture/2026-08-24-shell-tray-hide-to-tray`）：`Ryn.Plugins.Tray` 注册图标 + 菜单（显示主窗/检查更新/退出）；点击事件经 companion 中继（`__ryn.on` → `desktop.tray.event`）回宿主解析——`TrayService.EmitEvent` 是插件内部属性，AOT 下反射不可用。关窗默认取消并隐藏（`CloseGate` 唯一放行通道：托盘退出与自更新安装路径先批准再 Close）；托盘初始化失败时拦截不同步生效，关窗保持直退。

## 壳与窗口

* `src/DeepSeek.Harness.Desktop/DesktopBootstrap.cs`（组合根，原 `Program.cs` 已瘦身为薄壳）：`HarnessRuntimeHost.StartAsync(60s)` → `dsh web:` → `RynApplication.CreateBuilder().ConfigureOptions(opts.Url = webUrl)`。`ryn.json:identifier=io.github.ZK-Andy.dotnet-deepseek-harness-desktop` 与 `StartupWMClass` 同值，`Wayland/X11` 任务栏正确关联；`icon.png` 进 `AppContext.BaseDirectory` 并上 `hicolor/pixmaps`。
* `CurrentWindowAccessor`（Ryn.Core）供 `RuntimeSupervisor`、`PageHealthMonitor` 与后台随包插件任务做 `EvaluateJavaScriptAsync`/`NavigateAsync`。

## 运行时定位与启动

* **运行时来源 = 系统全局 node + 全局 dsh**（ADR `implemented/architecture/2026-08-31-simple-shell-single-global-dsh`）：安装器不携带运行时；dsh 版本探测走 PATH（`RuntimeVersionGate.ProbeAsync`，无独立 RuntimeLocator）。PATH 上无全局 dsh 时进入**首启引导**：`RuntimeBootstrap` 确保**系统全局 node**（复用 PATH 上用户 node/npm；无则桌面下载最新官方 node 装到系统全局前缀——默认 `~/.local`、写系统位需 sudo 时提示手动命令，不自备私有 node），用其 npm `npm install -g @deepseek-ai/dsh@alpha`（装/更新到 alpha 预发布通道，落到系统全局位），验证 `dsh --version` 可解析；dsh `npm install -g` 因权限需 sudo 时提示手动命令。失败进度页可见、可重试（`desktop.bootstrap.retry`）；引导落定前监督器与插件安装均被门控。
* dsh 版本只读探测：`Services/RuntimeVersionGate.ProbeAsync` 直跑 PATH `dsh --version`（全局 dsh 模型无捆绑形态），不维护任何下载运行时目录。
* `Services/HarnessRuntimeHost`：`ProcessStartInfo` 设 `DSH_HOME`、`pnpm_config_store_dir/cache_dir`（`DSH_HOME/.pnpm-store`）、`WorkingDirectory=AppContext.BaseDirectory`；`OutputDataReceived` 抓 `dsh web:` 的 `HarnessUrlParser`；`ErrorDataReceived` 留 `StderrTail` 8 行。`port 0` 首次 OS 分配并记忆，重启复用同端口保 `origin`，占位回退 `0`。
* `Services/HarnessUrlParser`：单行解析 `dsh web: http://127.0.0.1:<port>`。

## 单实例与退出

* `Services/LauncherActivation`：UDS 单实例仲裁（`$XDG_RUNTIME_DIR` 锁地址，dev 隔离同源）——首实例 `bind/listen` 持锁，launcher 二启发 `show` 命令请主实例显示主窗后退出；残留 socket 探活自愈，清理失败降级无监听主实例（绝不挡启动）。Windows 不启用。
* 托盘「退出」走有序编排：取消监督器 → `host.Stop()` 整树回收 dsh → marker Release → 关窗 → 8s 看门狗强制终结；端口被占回退 OS 分配时写漂移告警。

## 崩溃监督

* `Services/RuntimeSupervisor`：`WaitForExitAsync` + `CancellationToken` 循环；退出→`showRecovery`（`RecoveryScript` 覆写文档为“重连中”）→`host.RestartAsync(60s)`→`navigate(newUrl)`。仅重启子进程，不重启桌面进程；`supervisorCts` 随 `app.Run()` 结束取消。

## 插件装配与引导

* 随包插件清单：`dsh-desktop-companion`（桌面伴生：更新/诊断/设置 UI 与托盘事件中继，仅随包分发）——成员登记于 `Services/BundledPluginCatalog`；dshmarket 不再随包，改由首启引导经 registry 安装（`MarketInstallHelper.EnsureMarketFromRegistryAsync`，见下）。
* 首启引导（`RuntimeBootstrap`）：PATH 上无全局 dsh 时，在 spawn dsh **之前**完成「确保系统全局 node（复用 PATH 用户 node/npm；无则下载最新官方 node 装到系统全局前缀）→ 用其 `npm install -g @deepseek-ai/dsh@alpha`（系统全局位）→ 验证 `dsh --version`」，node/dsh 写系统位需 sudo 时给手动命令；全程进度页可见、失败可重试（`desktop.bootstrap.retry`）。
* **插件引导（ADR reference-alignment 批次二）**：运行时就位后、spawn dsh 前，若存在待装可选插件（现仅 `dshmarket` 预设），进度页呈现「插件准备」步（推荐 chip + 确认/跳过 + 安装日志回流）。用户确认才安装、跳过则该次不装（可经应用内市场补装）、5 分钟无决策默认跳过；companion（internal）不在勾选清单，保持 spawn 前静默自愈。
* 启动前 reconcile（`DesktopProfileBootstrap.ReconcileProfile`）：扫描 desktop profile，移除解析目标已不存在的本地 `file:`/`link:` bundle 引用（退役随包种子属之），对齐 dsh-tauri-desk #177——不允许不可解析 bundle 引用残留。
* **插件安装均在 spawn dsh 前完成**（对齐参照 `launch.rs`「所有插件内核前就位、绝不安装后重启」）：companion 经 `EnsureBundledPluginsBeforeSpawnAsync`——`BundledPluginCatalog.AssemblePending` 组装待装清单：未装即装（安装器资源 `resources/plugins` tgz，`ResolveCompanionSpec` `>1K` 校验）、已装则 `PluginVersionCheck` 版本感知升级（来源 > 已装副本即入列，同版/更高跳过；spec 缺失、解析器异常或脏版本串按单插件记日志跳过；见 ADR `implemented/feature/2026-08-25-bundled-plugin-version-aware-catalog` + `implemented/feature/2026-08-29-plugin-surface-consolidation`）；dshmarket 经 `EnsureMarketFromRegistryAsync`（`plugin add dshmarket@latest`）。安装前 `EnsureWorkspaceAllowBuilds` 放行 `allowBuilds` 6 项（`@deepseek-ai/dsh-subprocess-local/@google/genai/koffi/node-pty/protobufjs/esbuild`）、`CleanupBogusAppDependencyAsync` 清理 `0.1.10` 残留 `dependencies.app=file:...dshmarket.tgz`；装后 `EnsureBundlesContainsAsync` 兜底并补回桌面必需 bundle（`dsh-base`/`dsh-web-app`）。dev 运行且 DSH_HOME 显式覆盖指回真实 home 时整体跳过（防把 dev 依赖写进共享 profile）。
* `dsh` 的 `reconcilePlugins` 在 `plugin add` 后自动把包名追加到 `dsh.profile.bundles`（`pilot-harness` 同款）。
* **CLI shim 注册（ADR `implemented/architecture/2026-08-31-simple-shell-single-global-dsh`）**：dsh 已全局在 PATH，`CliShimRegistrar.TryRegister` 只注册内容恒定的 `pnpm` shim——Windows `%LOCALAPPDATA%\deepseek-harness\bin` 写 `pnpm.cmd`/`pnpm.ps1` + `HKCU\Environment\Path` 幂等合并 + `WM_SETTINGCHANGE` 广播；mac/linux `~/.local/bin` 写 POSIX sh（`pnpm`）+ 既有 `.bashrc`/`.zshrc`/`.profile`/`.bash_profile` 幂等 rc 块。shim 不烘焙运行时/DSH_HOME；`pnpm` 优先转发用户自装的同名命令（排除本 shim 目录），绝不覆盖用户配置（目标为本应用生成的 shim 才覆盖、悬空符号链接先移除、用户文件保留）。best-effort——任一步失败仅告警不阻启动。本分布不捆绑独立 pnpm（dsh 的 `plugin` 子命令经 `spawnSync("pnpm")` 从 PATH 调用），故 pnpm shim 只转发用户自家 pnpm、缺则诚实提示（参照项目 `dependencies/pnpm` 属有意差异）。

## 外部链接接管

* 宿主侧 `Services/RynNavigationCallbacks`（`Ryn.Callbacks`，Ryn 0.32.0）在导航边界统一裁决——`[RynCallback(WebViewNavigating)]`：**用户发起**（`IsUserInitiated`）的站外绝对 http(s) → `NavigationDecision.Block` + 经共享 `SystemBrowser` 开系统浏览器；同源 SPA 路由 / `ryn://` / `data:` / 非 http(s) / 宿主导航（崩溃恢复）放行。`[RynCallback(WebViewNavigated)]` 把当前 origin 刷新为实际到达 URL 的 origin、留痕并回调「页面已到达」信号（供启动横幅门控）。`ConfigureServices` 注册 `AddRynCallbacks()` + `AddRynNavigationCallbacks()`（源生成）。相较旧点击层 hack，**覆盖一切导航**（`window.location`/`window.open()`/`<form>` 等非点击路径），不再依赖前端注入捕获脚本。打开失败（`SystemBrowser` 返回 false/抛异常）时经 `EmitEvent("desktop.externalLinkOpenerFailed")` 推事件给页面，companion 渲染 toast（ADR `implemented/feature/2026-08-28-external-link-opener-failure-toast`；外部链接拦截本体见 `implemented/feature/2026-08-28-ryn-navigation-callbacks`）。
* 宿主侧 `Services/ExternalLinkCommandRouter`（`ICommandRouter`）收 `app.openExternal` 命令，经 `ExternalLinkPolicy.IsExternalHttpLink` 复核后经 `SystemBrowser`（Linux `xdg-open` / 其余 `Process.Start(UseShellExecute)`）开系统浏览器——给已发布旧版 companion 与 Ryn 命令面保留的落地点。
* companion `client.js` 不再注入 capture 点击监听（外链接管已迁宿主导航层）；`__ryn_externalLinkCatcher` / `__dshDesktopCompanionLinks` 双旗认领机制退役。

## 自更新

* 状态机 `Services/Update/UpdateStateMachine`（移植 opencode updater-controller）：`idle→checking→downloading→ready→installing` + up-to-date/error；检查/下载/安装委托注入 + ready 持久化接口，纯逻辑可单测。启动对账（记录版本不高于当前或损坏 → 清记录）后自动检查一次，失败静默转 error；并发检查互斥。
* Feed：`releases.atom` 最新稳定 tag + `expanded_assets/<tag>` 抓资产 href（绕 api 限流）；`ReleaseMeta.Pick` 按 RID 后缀挑资产。下载 `.part` 原子改名 + SHA256SUMS 强校验（**release 未附校验文件或 HTTP 非 2xx 时 fail loud 拒装**）→ `<DSH_HOME>/updates/`。
* 安装：Linux pkexec 脚本（等本进程退出→dpkg/rpm→runuser 降权拉起新版）；Windows Inno `/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`；macOS v1 报错引导手动。
* UI：伴生插件注册 `sidebar.footer.action`（侧栏底部设置入口上方动作行）；**仅 ready 渲染**圆形下载钮，hover 展开版本文字，点击即装+重启；installing 转圈禁点。伴生插件另注册单一 `settings.section`「桌面设置」页（order 50，市场之后；ADR `implemented/bug-fix/2026-08-24-companion-settings-consolidation`）：更新块（当前版本 + 手动检查按钮 + 完整状态行，error 显宿主传回原因，无自更新栈降级为页内不可用提示）+ 诊断导出块 + 开机自启开关三块合一页。状态经宿主 CustomEvent `dsh-desktop-update` 推送，初值走 `ryn.invoke('desktop.update.getState')`；状态帧含 `current`（当前版本）与 error 态 `message` 字段。伴生插件客户端文案已接入 dsh client i18n（`@deepseek-ai/dsh-client-locale` 的 `zh`/`en` 字典），随 dsh 语言切换中⇄英（ADR `implemented/feature/2026-08-28-companion-client-i18n`）。
* 参数：appsettings.json `Update` 节（Repository/超时/目录）；当前版本 = csproj `<Version>`（发布 CI 以 `-p:Version=` 覆盖，输入留空回退 csproj、空值 fail loud）。**dev 运行时不装载自更新栈**（除非 `DSH_DESKTOP_UPDATE_FORCE=1` 显式开启）。

## 打包

* 依赖全局 dsh（ADR `implemented/architecture/2026-08-31-simple-shell-single-global-dsh`）：安装器**不捆绑运行时闭包**，只带壳（publish 全量，实测 ~26-36MB 压缩后）+ 安装器自带插件资源 `resources/plugins/dsh-desktop-companion.tgz`（`scripts/build-companion-tgz.sh` 打包时从仓库源码现打并校验）；运行时 = 用户 PATH 上的全局 dsh（无则首启引导 `npm install -g @alpha` 装，见「运行时定位与启动」）。
* `scripts/package-linux.sh`：`dotnet publish -r linux-x64|linux-arm64`（arm64 自 Ryn.Interop 0.30.4 供给 linux-arm64 native 起恢复发布，2026-08-25）→ `staging` 现打 companion tgz 进 `resources/plugins`、拦 `resources/runtime` 闭包残留（fail loud）→ `deb (Depends: libwebkitgtk-6.0-4, libadwaita-1-0, arch amd64/arm64)` / `rpm (AutoReqProv:no, Requires: libwebkitgtk-6.0.so.4()(64bit), libadwaita-1.so.0()(64bit), BuildArch x86_64/aarch64)`。
* `scripts/package-macos.sh` / `package-windows.sh`：`dotnet publish -r osx-(x64|arm64)/win-x64` → `staging` 校验 → 单一安装产物：mac `dmg`（`hdiutil`，含 `.app`）/ win `exe` 安装器（`Inno Setup`/`NSIS`/`7z SFX`，`…-setup.exe`），文件名含 `…_macos-*/…_windows-*` 标识，签名占位（`codesign`/`signtool` 待证书）。**不单独产出便携 zip**（对齐 pilot-harness 每平台单产物的思路）。
* 安装器资源一律 **exe 目录相对**（`AppContext.BaseDirectory/resources/plugins`，Linux `usr/lib/<app>` / mac `Contents/MacOS` / win 安装根三平台同构）；`verify-package-layout.sh` 断言无闭包残留 + 插件 tgz 名称/体积关；`CI`：`package-linux/macos/windows.yml` 只出包 + 上传 `7 天 Artifacts`；统 **`release.yml`**（`tag v*`）聚合三平台产物 → 合并 `SHA256SUMS` → 用 `scripts/release-notes.sh` 生成结构化正文，幂等创建单个 `Release`（单一 owner，不再并行重复）。

## 配置与扩展

* `appsettings.json`：`DevTools:false`（`DSH_DEVTOOLS=1` 开启）；`Update` 节（自更新仓库/超时/目录）。
* `ryn.json`：`identifier/capabilities`。
* 扩展点：`DSH_DESKTOP_RUNTIME_DIR` / `DSH_DESKTOP_DSH_HOME` / `DSH_DESKTOP_UPDATE_FORCE`（dev 下显式开启自更新）覆盖。
* **开发运行时隔离**：`DSH_DESKTOP_RUNTIME_DIR` 或 `DSH_DESKTOP_DEV=1` 显式标记即进入 dev 模式（判定不探测闭包存在性）——ApplicationId 自动加 `.dev` 后缀（与已装正式版可同时开窗，避开 GTK 同 id 单实例互斥），DSH_HOME 未显式覆盖时自动指向 `<仓库>/.cache/dev-home`；显式指回真实 home 时随包插件安装自动跳过防串扰。
