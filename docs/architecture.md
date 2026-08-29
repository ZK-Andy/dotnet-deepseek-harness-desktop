# Architecture

> 现状。`Ryn` 壳 + online-first 运行时引导（无捆绑闭包）+ 崩溃监督 + 插件后台安装 + pilot-harness 打包（ADR `proposed/architecture/2026-08-29-online-first-unbundled-runtime`）。

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
* 运行时来源见「运行时定位与启动」的 online-first 条目（无捆绑时回退 `PATH dsh`，再缺失走首启引导）。
* **可观测性**（ADR `2026-08-24-shell-observability-diagnostics`）：全部壳侧诊断经 `HostLog` 双写 stdout 与 `<home>/logs/host.log`（超 5MB 滚动 .old）；supervisor 恢复时落子进程 stderr 尾部、自更新状态机每次变化留痕；`RunMarker` 启动占位/owner 清理判定非受控退出（横幅提示）；`desktop.diagnostics.export` + CLI `--export-diagnostics` 导出白名单诊断 zip 到用户文档目录。
* 启动期告知（ADR `implemented/architecture/2026-08-23-shared-home-desktop-profile`）：`RuntimeVersionGate` 只读探测 dsh 版本低于底线仅横幅提示不阻断；检测到 v0.2.x 私有 home 残留则在 host.log 留痕（界面横幅已去除，见 ADR `implemented/bug-fix/2026-08-24-companion-settings-consolidation`）。
* **系统托盘与 hide-to-tray**（ADR `implemented/architecture/2026-08-24-shell-tray-hide-to-tray`）：`Ryn.Plugins.Tray` 注册图标 + 菜单（显示主窗/检查更新/退出）；点击事件经 companion 中继（`__ryn.on` → `desktop.tray.event`）回宿主解析——`TrayService.EmitEvent` 是插件内部属性，AOT 下反射不可用。关窗默认取消并隐藏（`CloseGate` 唯一放行通道：托盘退出与自更新安装路径先批准再 Close）；托盘初始化失败时拦截不同步生效，关窗保持直退。

## 壳与窗口

* `src/DeepSeek.Harness.Desktop/Program.cs`：`HarnessRuntimeHost.StartAsync(60s)` → `dsh web:` → `RynApplication.CreateBuilder().ConfigureOptions(opts.Url = webUrl)`。`ryn.json:identifier=io.github.ZK-Andy.dotnet-deepseek-harness-desktop` 与 `StartupWMClass` 同值，`Wayland/X11` 任务栏正确关联；`icon.png` 进 `AppContext.BaseDirectory` 并上 `hicolor/pixmaps`。
* `Services/CurrentWindowAccessor` 供 `RuntimeSupervisor` 与后台随包插件任务做 `EvaluateJavaScriptAsync`/`NavigateAsync`。

## 运行时定位与启动

* **online-first 运行时来源**（ADR `proposed/architecture/2026-08-29-online-first-unbundled-runtime`）：安装器不携带运行时；`RuntimeLocator.TryLocateRuntimeDirectory` 按「捆绑目录（`DSH_DESKTOP_RUNTIME_DIR` / `resources/runtime`，dev/存量场景）→ 引导下载目录 `~/.dsh-desktop/runtime`」解析。捆绑与 PATH `dsh` 均缺失时进入**首启引导**：`RuntimeBootstrap` 状态机复用本机 Node（≥底线主版本）或下载钉版 Node（nodejs.org dist + SHA256 校验 + 解压归一），`npm install @deepseek-ai/dsh@latest`，每步完成即验证产物，失败进度页可见、可重试（`desktop.bootstrap.retry`）；引导落定前监督器与插件安装均被门控。
* `Services/RuntimeLocator`：`TryLocateBundled` 判 `node(.exe)` + `node_modules/@deepseek-ai/dsh/lib/bin.js`（捆绑与引导下载同布局）。
* `Services/HarnessRuntimeHost`：`ProcessStartInfo` 设 `DSH_HOME`、`pnpm_config_store_dir/cache_dir`（`DSH_HOME/.pnpm-store`）、`WorkingDirectory=AppContext.BaseDirectory`；`OutputDataReceived` 抓 `dsh web:` 的 `HarnessUrlParser`；`ErrorDataReceived` 留 `StderrTail` 8 行。`port 0` 首次 OS 分配并记忆，重启复用同端口保 `origin`，占位回退 `0`。
* `Services/HarnessUrlParser`：单行解析 `dsh web: http://127.0.0.1:<port>`。

## 单实例与退出

* `Services/LauncherActivation`：UDS 单实例仲裁（`$XDG_RUNTIME_DIR` 锁地址，dev 隔离同源）——首实例 `bind/listen` 持锁，launcher 二启发 `show` 命令请主实例显示主窗后退出；残留 socket 探活自愈，清理失败降级无监听主实例（绝不挡启动）。Windows 不启用。
* 托盘「退出」走有序编排：取消监督器 → `host.Stop()` 整树回收 dsh → marker Release → 关窗 → 8s 看门狗强制终结；端口被占回退 OS 分配时写漂移告警。

## 崩溃监督

* `Services/RuntimeSupervisor`：`WaitForExitAsync` + `CancellationToken` 循环；退出→`showRecovery`（`RecoveryScript` 覆写文档为“重连中”）→`host.RestartAsync(60s)`→`navigate(newUrl)`。仅重启子进程，不重启桌面进程；`supervisorCts` 随 `app.Run()` 结束取消。

## 随包插件后台安装

* 随包插件清单：`dshmarket`（市场，registry 有上游）与 `dsh-desktop-companion`（桌面伴生：更新/诊断/设置 UI 与托盘事件中继，仅随包分发）——成员登记于 `Services/BundledPluginCatalog`，清单是唯一扩展点。
* `Program.cs` 后台任务（`Task.Delay 3s`，不阻塞首启窗口）：
  0. dev 运行（`DSH_DESKTOP_RUNTIME_DIR` 或 `DSH_DESKTOP_DEV=1` 显式标记，打包产品两者皆无）且 DSH_HOME 为显式覆盖指回真实 home 时整体跳过——防把 dev 依赖写进共享 profile；自动隔离（`.cache/dev-home`）的 dev home 与正式版无涉，正常安装。
  1. `BundledPluginCatalog.AssemblePending` 清单逐项组装待装清单：未装即装（本地来源 file: / registry 回退 `@latest`）；已装按 profile dependencies 的 spec 形态分流——registry 形态（非 `file:`/`link:`）**完全放手**跳过（与自装等价，即便来源版本更高也不回拉）；本地形态则 `PluginVersionCheck` 比对来源 tgz 内 `package/package.json` 的 version 与 profile `node_modules` 副本 version，来源更新即入列（离线路径），同版或更高且清单项开启归化（dshmarket 开、companion 关）时改为入列 **registry 归化条目**（spec = `裸包名@latest`——裸名对既有依赖是 pnpm 幂等 no-op，显式 @latest 才强制改写 spec；装后与自装完全等价；失败下次启动重试，幂等）（改清单内插件必须 bump version，否则不触发升级；spec 缺失、解析器异常或脏版本串按单插件记日志跳过；见 ADR `implemented/feature/2026-08-25-bundled-plugin-version-aware-catalog` + `implemented/feature/2026-08-29-bundled-plugin-registry-normalization`）。
  1b. 清理 `0.1.10` 残留 `dependencies.app=file:...dshmarket.tgz`。
  2. `EnsureWorkspaceAllowBuilds` 把 `pnpm-workspace.yaml` 的 `allowBuilds` 6 项（`@deepseek-ai/dsh-subprocess-local/@google/genai/koffi/node-pty/protobufjs/esbuild`）置 `true`。
  3. spec 解析：市场走 `ResolveMarketSpec`（运行时目录 tgz >10K → 目录 → 无本地来源回退 `dshmarket@latest` registry 直装，无钉版）；伴生走 `ResolveCompanionSpec`（安装器自带 `resources/plugins` tgz `>1K` → 运行时目录 tgz → 目录 → 无即跳过，无 registry 分发面）。
  4. 分组 spawn `bundled node dsh/lib/bin.js plugin --profile desktop add <spec…>` 多包安装（注入 `DSH_HOME/.pnpm-store`）：本地路径 spec（随包 tgz/目录，离线可靠）先装、registry 触碰条目（归化/registry 回退首装）后装——pnpm 单事务多 spec 一败俱败，离线时归化失败不得拖累本地路径安装；`exit 0` 后对每项 `EnsureBundlesContainsAsync(pkg)` 兜底，并补回桌面必需 bundle（`dsh-base`/`dsh-web-app`）。
  5. `EvaluateRecovery + host.Stop()` 交 `RuntimeSupervisor` 重启并导航新 `URL`。
* `dsh` 的 `reconcilePlugins` 在 `plugin add` 后自动把包名追加到 `dsh.profile.bundles`（`pilot-harness` 同款）。

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

* online-first（ADR `proposed/architecture/2026-08-29-online-first-unbundled-runtime`）：安装器**不捆绑运行时闭包**，只带壳（publish 全量，实测 ~26-36MB 压缩后）+ 安装器自带插件资源 `resources/plugins/dsh-desktop-companion.tgz`（`scripts/build-companion-tgz.sh` 打包时从仓库源码现打并校验）；运行时由首启引导安装（见「运行时定位与启动」）。
* `scripts/package-linux.sh`：`dotnet publish -r linux-x64|linux-arm64`（arm64 自 Ryn.Interop 0.30.4 供给 linux-arm64 native 起恢复发布，2026-08-25）→ `staging` 校验插件资源、拦闭包残留 → `deb (Depends: libwebkitgtk-6.0-4, arch amd64/arm64)` / `rpm (AutoReqProv:no, Requires: libwebkitgtk-6.0.so.4, BuildArch x86_64/aarch64)`。
* `scripts/package-macos.sh` / `package-windows.sh`：`dotnet publish -r osx-(x64|arm64)/win-x64` → `staging` 校验 → 单一安装产物：mac `dmg`（`hdiutil`，含 `.app`）/ win `exe` 安装器（`Inno Setup`/`NSIS`/`7z SFX`，`…-setup.exe`），文件名含 `…_macos-*/…_windows-*` 标识，签名占位（`codesign`/`signtool` 待证书）。**不单独产出便携 zip**（对齐 pilot-harness 每平台单产物的思路）。
* 安装器资源一律 **exe 目录相对**（`AppContext.BaseDirectory/resources/plugins`，Linux `usr/lib/<app>` / mac `Contents/MacOS` / win 安装根三平台同构）；`verify-package-layout.sh` 断言无闭包残留 + 插件 tgz 名称/体积关；`CI`：`package-linux/macos/windows.yml` 只出包 + 上传 `7 天 Artifacts`；统 **`release.yml`**（`tag v*`）聚合三平台产物 → 合并 `SHA256SUMS` → 用 `scripts/release-notes.sh` 生成结构化正文，幂等创建单个 `Release`（单一 owner，不再并行重复）。

## 配置与扩展

* `appsettings.json`：`DevTools:false`（`DSH_DEVTOOLS=1` 开启）；`Update` 节（自更新仓库/超时/目录）。
* `ryn.json`：`identifier/capabilities`。
* 扩展点：`DSH_DESKTOP_RUNTIME_DIR` / `DSH_DESKTOP_DSH_HOME` / `DSH_DESKTOP_UPDATE_FORCE`（dev 下显式开启自更新）覆盖。
* **开发运行时隔离**：`DSH_DESKTOP_RUNTIME_DIR` 或 `DSH_DESKTOP_DEV=1` 显式标记即进入 dev 模式（判定不探测闭包存在性）——ApplicationId 自动加 `.dev` 后缀（与已装正式版可同时开窗，避开 GTK 同 id 单实例互斥），DSH_HOME 未显式覆盖时自动指向 `<仓库>/.cache/dev-home`；显式指回真实 home 时随包插件安装自动跳过防串扰。
