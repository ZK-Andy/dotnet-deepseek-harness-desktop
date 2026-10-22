# Agent Note: Avalonia G0——入口切到 Avalonia 宿主

Status: implemented

Review: FULL/2026-10-22/R1=ok R2=ok R3=ok

## Problem

宿主迁移提案（[avalonia-host-migration](../../proposed/architecture/2026-10-22-avalonia-host-migration.md)）已定稿，但默认入口仍是 Ryn：`Program.Main` 直接构造 `DesktopAdapter`，整条启动链（`RynApplication.CreateBuilder().Build()` → `StartupSequence.RunMainLoop()`）把窗口、托盘、顶栏、桥与主循环绑在 Ryn 上。迁移若停在文档，G1–G3 的每一片都无从真实运行验证；Ryn 上游主分支自 2026-09-13 停滞，等待没有止损点。

首片必须回答的问题是：能否用最小宿主替换，把既有的 dsh spawn 与回环代理原样接到 Avalonia 窗口上，并真实跑到 dsh UI。

## Decision

入口切到 Avalonia 宿主，dsh 侧链路一律复用不重写：

- `Program.Main` 的壳路径改为 `Hosting.AvaloniaHost.Run(args)`；诊断 CLI 前置路径不变。
- 新增壳工程域目录 `Hosting/`：`AvaloniaHost.cs` 作为装配面进 `verify-compose-root.py` 的 `ASSEMBLY_SURFACE_EXTRA`（受 C1/C2 方向闸，不进域清单）；`ShellApplication` / `ShellStartup` / `ShellWindow` 登记进 `scripts/domain-files.manifest.json` 的 C8 在册清单。
- 包：`Avalonia.Desktop` 12.1.4、`Avalonia.Themes.Fluent` 12.1.4、`Avalonia.Controls.WebView` 12.1.1（CPM 单一版本源）。
- 启动链复用：`PreBuildRegistration.CreateSharedState` / `CreatePreflight` / `CreateRuntimeStarter`、`RuntimeStarter`（Core 用例）、`HarnessRuntimeHost`、`DshLoopbackProxy` 原样接入，崩溃 marker 用 `RunMarker.Acquire/Release` 补齐；启动前置 `ProfileLifecycle.EnsureReady` 与 `CompanionPreSpawn.EnsureInstalled` 按旧编排偏序在后台相执行（两者均 headless，不需要页面反馈）；单实例仲裁复用 `LauncherActivation.TryBindPrimary`（唤回回调 no-op）；`ShellResources` 仍是进程级资源唯一释放者，`AvaloniaHost.Run` 的 `finally` 释放一次。
- 窗口先亮：`ShellWindow` 挂 `NativeWebView`，初始 URL 是壳 origin（代理 `Url`）；`Opened` 后 `ShellStartup.Begin` 在后台跑 spawn 与铸币，页面经代理自行加载。`DSH_DEVTOOLS=1` 映射到 `EnvironmentRequested.EnableDevTools`。
- 本片边界：不接托盘、顶栏、`window.__shell` 桥、监督器、恢复面、更新 UI 与首启引导（无全局 dsh 时的自动安装随 G1）；Ryn 装配面保留在仓库内但不被入口调用（回退路径 = `git revert` 本片，ADR 不设运行时开关）。
- 站外导航在壳侧兜底：`ShellWindow` 在 `NavigationStarted` 取消非壳 origin 的导航，`NewWindowRequested` 一律不弹新窗；打开系统浏览器与 `app.openExternal` 复核随 G2 接回。

## Alternatives considered

- **Program 继续走 Ryn，等 G1–G3 齐了再切入口**：迁移各片无法在真实入口上验证，每片只能靠单测与推断；迁移验收点被推迟到最后一次性暴露。落败。
- **开 spike 分支或加宿主开关先探路**：迁移 ADR 已明确不设 spike 分支、不设宿主开关；双入口会让评审面、测试面与打包面同时分叉。落败。
- **一次性大爆炸重写整个装配面再切入口**：无法逐片回退，单次 diff 不可评审，G0 的「跑起来」信号被埋到最后。落败。
- **先删 Ryn 代码再切入口**：G1–G3 还需要 Ryn 侧的托盘/桥/监督实现作参照；先删会丢掉迁移期的对照组。落败（本片保留 Ryn 代码，仅不调用）。

## Consequences

- G0 期间没有托盘、顶栏、命令桥与崩溃监督：页面里对 `window.__ryn.invoke` 的调用（自绘顶栏、companion 中继、窗口命令）会失败，关窗即退出进程，dsh 崩溃后页面停在断连态。这些是 G1–G3 的交付项，不是本片缺陷，但用户可感知。
- 入口切换后 Ryn 装配面不再被主线执行，`DesktopAdapter`/`RynOptionsProfile`/`PageBridge` 仍编译在册；两套宿主代码并存期按迁移 ADR 逐片收口，Ryn 包引用在 G1–G3 完成前不删。
- 增量闸豁免：gross ≤ 600，fanout ≤ 15，packages ≤ 3（三个新增包 id：`Avalonia.Desktop`、`Avalonia.Themes.Fluent`、`Avalonia.Controls.WebView`；本 ADR 即本变更集内的一次性依据）。豁免通道与 `gross`/`fanout` 同机制：标记须是本次新增行，批次入库后自动失效。
- 根集债：真实入口 `Hosting/AvaloniaHost.cs` 已进装配面（C1/C2 方向闸覆盖），但 C3/C4/C5/F3 的根集仍是 `Program` + `DesktopAdapter*`。未重绑的直接原因是 C4 要求「根集恰一处同步桥」，而 Avalonia 的同步 lifetime 不需要该桥；根集重绑与 C4 语义重议随 G1。
- 关窗回收（窗口关闭 → `ShellStartup.Dispose` 取消并有限等待 → `ShellResources` 停 dsh → `RunMarker.Release`）只经代码路径核对，未做自动化验证；本机实跑用的是超时终止路径。
- 单实例只做互斥，不接唤回：第二实例会被通知后退出，但主窗不弹起（G1）。
- `__ryn` 桥缺失时页面侧是静默退化：`desktop.ui.getLocale` 被页面自身 catch、`retryBootstrap` 空转、companion 中继被 `window.__ryn &&` 跳过，host.log 无对应记录（G2 桥接回后消失）。
- `PermissionRequested` / `DownloadStarting` 未接管：三端默认各异，附件下载、通知与媒体权限在 G0 可能静默失败或不受控弹窗，parity 留 G4/G5。
- 预检（`FirstBootBootstrapService.Resolve` 的同步 dsh 版本探测，上限 8s）先于窗口创建，「窗口先亮」只在预检完成后成立。
- README 的首启自动安装与崩溃监督描述对应 0.6.14 发布行为；main 的 G0 中间态不具备这两条，0.7.0 前不据 main 出补丁（迁移 ADR 的定版口径）。
- 本机验证只覆盖 Linux Wayland；Windows WebView2 运行时缺失路径、macOS WKWebView 与 Linux 双后端（WPE/WebKitGTK 回退）差异留 G4/G5。

## Testing

- 本机实跑（Wayland，临时 `DSH_HOME`）：窗口创建 → `dsh web = http://127.0.0.1:<port>` → 铸币 303 + `dsh-auth` cookie → 就绪探活连续 3 次 → 代理记录 `101 Switching Protocols`（`/api/remote.mux`），即页面已加载并连上 dsh。
- 单实例实跑（隔离 `XDG_RUNTIME_DIR`）：主实例绑定后第二实例被通知并退出（主实例日志「单实例请求：show / 处理完成」）。
- `dotnet build`：0 警告 0 错误；`dotnet test`：1096/1096 通过（6 + 234 + 240 + 616；新增 `ShellStartupTests` 三例覆盖单次触发、在途取消、未触发即释放）；覆盖率 66.51%（CI 同命令本地并集，基线随之更新）。
- 门禁：compose-root / registration-discipline / host-command-ids / core-purity / code-health / governance / ui-copy / md-links / doc-budgets / adr-format 全绿；增量闸在本次一次性豁免内（`gross`/`fanout`/`packages` 三帽均未越界）。

## Related

- [avalonia-host-migration](../../proposed/architecture/2026-10-22-avalonia-host-migration.md)：迁移总提案（G0–G5 阶段与边界）。
- [webview-shared-memory-rendering](../../implemented/architecture/2026-10-18-webview-shared-memory-rendering.md)：被迁移的 Ryn/WebKitGTK 内存现状。
