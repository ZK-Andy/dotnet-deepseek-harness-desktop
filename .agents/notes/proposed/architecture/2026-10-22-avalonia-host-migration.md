# Agent Note: Avalonia 宿主迁移（Ryn → Avalonia 12.1.x）

Status: proposed

## Problem

宿主层 Ryn 0.38.0 已不再交付演进：上游 `Yupmoh/Ryn` 主分支最近提交与最新 release 均为 2026-09-13（v0.38.0），2026-10-19 的推送来自 feature/dependabot 分支而非 main；单维护者、28 star。Linux WebKitGTK 匿名内存增长（上游 issue #81）最后一条维护者回复停在 2026-08-30，本仓只能以 `LinuxRenderingMode.SharedMemory` 缓解（ADR `2026-10-18-webview-shared-memory-rendering`，n=1【探索性】）。

上游停滞后，任何绑定层增强（例如调用 `webkit_memory_pressure_settings_*` 给 WebProcess 设上限）都只能靠 fork 或自持原生绑定；宿主替换窗口事实上只剩「自持」或「换宿主」两条路。

本仓与 Ryn 的耦合面：主工程 44 个文件、Core 7 个、Infrastructure 6 个、测试 23 个文件；页面侧命令通道写死 `window.__ryn.invoke`（companion `client.js`、`CaptionBar.cs`、`wwwroot/index.html`）。

## Proposal

**决定：宿主层从 Ryn 迁到 Avalonia 12.1.x + `Avalonia.Controls.WebView` 12.1.1（MIT）。**

- TFM 维持 `net10.0`（Avalonia 12 的最低支持版本恰为 .NET 10，见官方支持矩阵）；发运形态维持 JIT self-contained，AOT 决策不变。
- Linux 运行时换栈：渲染面 **WPE WebKit 优先**（离屏合成进 Avalonia 视觉树）；WPE 缺失时 Avalonia 自动回退 WebKitGTK 4.1，故基线依赖是 GTK3 + `webkit2gtk-4.1` + `libsoup3`（Avalonia 不支持 WebKitGTK 6.0 的 GTK4 构建）。后端选择保持 Avalonia 官方默认，不自带 WPE 运行时、不硬依赖 WPE。deb `Depends:` / rpm `Requires:` 从 `libwebkitgtk-6.0-4` + `libadwaita-1-0` 换成 GTK3 栈；Ubuntu 不打包 WPE。
- 只换宿主与桥接：保留 dsh Web 客户端与 JS 插件生态；不转原生界面；不承诺内存下降。
- JS↔C# 走包自带消息桥（`WebMessageReceived` / `InvokeScript`），实现 `window.__shell` 替换 `window.__ryn`：`invoke` 由 `WebMessageReceived` 承接，且消息须校验来源为壳 origin（等价旧 `AllowedOrigins`/`AuthorizeOrigin` 面，否则外源页面可直投命令帧）；Avalonia 无 host→page 订阅语义，`on` 与 `__shell_titlebar` 由壳用 `InvokeScript` 主动派发（companion 托盘中继、顶栏脚本各自改消费新通道）；`ICommandRouter` / `CommandFrame` 契约与命令 ID 不变，窗口命令（startDrag / toggleMaximize / close）走同一通道。
- 命令面在新宿主的实际形状少一条：`desktop.tray.event` 只留在旧宿主装配面（Avalonia 托盘菜单是原生回调，
  页面侧没有事件源），见 [`avalonia-g3-exit-restart`](../../implemented/architecture/2026-10-23-avalonia-g3-exit-restart.md)。
- `ryn.json` 能力沙箱随 Ryn 退役：等效面收窄为命令注册表——未注册命令一律拒绝（ID 单一事实源与壳侧复写闸见 `verify-host-command-ids.py`），补一条「未注册 ID 被拒」回归；README 的 deny-by-default 表述改为按命令表描述。
- `DSH_DEVTOOLS=1` 映射到 `EnvironmentRequested.EnableDevTools`，仅保留开发开关，不进 parity 范围。
- dsh 的 `inspector-profile` / `session-inspector` / `inspector` 系列是上游 Web 插件，随 dsh 页面运行，不属于壳迁移范围。

**证据（不再逐能力自测）**：`NativeWebView` 已在生产项目中使用——YoutubeDownloader（NativeWebView + cookie 管理 + `NavigationStarted` + devtools + 每引擎环境参数）、UniGetUI（WebView2 运行时检测与降级）、Libation / Pixeval / Everywhere（桌面发行）；`TrayIcon` 与 `ExtendClientAreaToDecorationsHint` 是 Avalonia 核心控件。控件 API 面包含 `NavigationStarted` / `NavigationCompleted` / `NewWindowRequested` / `WebMessageReceived` / `WebResourceRequested` / `EnvironmentRequested` / `TryGetCookieManager` / `InvokeScript` / `TryGetPlatformHandle`。

**阶段（按目标状态，约 7–8 人周）**

| 阶段 | 目标状态 | 估算 |
|---|---|---|
| G0 | 跑起来：Avalonia 窗口 → NativeWebView → 复用既有 spawn/代理 → dsh UI 可见 | 1–2 天 |
| G1 | 壳齐：托盘/关窗到托盘、单实例、无边框顶栏、失败面对话框、恢复页 | 1 周 |
| G2 | 桥通：`av-webview` 替换 `__ryn`，PagePump、健康探针、外链拦截 | 1.5 周 |
| G3 | 面齐：11 个命令路由（`desktop.tray.event` 随新宿主退役）、引导/首启、locale、更新 UI、诊断、companion | 2 周 |
| G4 | 能发：三平台打包/更新/CI/smoke，WebView2 运行时缺失检测 | 1.5–2 周 |
| G5 | 对齐回滚：0.6.14 parity、浸泡基线、0.7.0 | 1 周 |

**执行序：G0 → G1 首批 → G2 → G1 余项 → G3 → G4 → G5。** 顶栏与恢复页的页面侧脚本依赖 `window.__shell`，桥必须先于消费方落地，故 G2 先于 G1 余项；阶段编号不改名（ADR、待办与 journal 已按编号引用）。G2 已落地（[avalonia-g2-shell-bridge](../../implemented/architecture/2026-10-22-avalonia-g2-shell-bridge.md)）；G1 余项中页面侧（顶栏/恢复页）是桥的纯消费方，失败面对话框走原生窗口、不涉桥。

## Alternatives considered

- **维持 Ryn（pin 0.38.0 或 fork）**：零迁移成本；但上游停止交付，内存与 WebKitGTK 新能力无解，fork 需自持 C# 绑定 + 原生归档工具链。落败。
- **自持平台 WebView 绑定（WebKitGTK / WebView2 / WKWebView P/Invoke）**：可控性最高，可直接调用 `webkit_memory_pressure_settings_*`；但等于重写一遍 Ryn 的绑定与插件面，成本与收益不匹配。落败。
- **打包 Chromium（CEF / DotNetBrowser）**：唯一能真正摆脱 WebKitGTK 的路径；但安装器 +150–250 MB、Chromium 安全跟版、.NET 跨平台绑定薄弱（CefSharp 仅 Windows、CefNet 停滞、DotNetBrowser 商业）。当前非必要，落败。
- **Tauri / Uno / MAUI**：Tauri 在 Linux 同为 WebKitGTK 且换语言栈；MAUI 无 Linux 支持；Uno 生态弱。落败。
- **直接转原生界面**：割裂 dsh 的 JS 客户端插件生态；且 Avalonia 本身可作为将来原生 UI 的底座，无需在本阶段决定。落败（本阶段）。
- **继续等待上游修复**：issue #81 自 2026-08-30 无进展、主分支停滞，等待无止损点。落败。

## Consequences

- 0.6.14（Ryn 宿主）保留为回滚产物；0.7.0 为 Avalonia 首发。
- 0.6.14 定版：迁移期间不再出补丁版，非迁移工作排在迁移之后；迁移在 `main` 逐片落地——不设 spike 分支、不设宿主开关，每片自带文档与测试，失败用 revert 或 fix-forward。
- 内存不承诺下降：Linux 首选 WPE、缺失时回退 WebKitGTK 4.1，dsh 进程树 636 MB 的实测（node 452 MB + codegraph 三进程 184 MB，n=1【探索性】）含 codegraph 三进程；该组件不在当前环境，184 MB 部分不可复用，node 的 452 MB 仍只有 n=1。
- Windows 依赖 WebView2 运行时：复用现有「缺时提示自装」路径，并按 UniGetUI 的实机教训补检测（缺失时回退旧 EdgeHTML 会在 `async void` 续体里崩进程）。
- 打包面变化：`verify-package-layout.sh` 的 `libsaucer*` / `libryn-pty` 断言改为 Avalonia 原生库清单；Linux 包依赖换 GTK3 栈（见 Proposal），`libadwaita-1-0` 可删；更新 feed 与三平台 smoke 同步修改。
- 文档随片同批改，不留到迁移完成：每个改变宿主行为的提交同时改对应文档——`docs/architecture.md`（技术栈与启动链）、`docs/development.md`（构建、`DSH_DEVTOOLS`、Linux 依赖清单）、`docs/testing.md`（打包校验清单、本机冒烟行）、`docs/cookbook.md`（Ryn/WebKitGTK 条目）、`docs/architecture-standards.md`（工程表、R3 例外、R1 的「Ryn 无 hosted-service」前提、`ShellResources` 补 Ryn 缺口的理由）、`docs/faq.md`/`faq.en.md` 与 `docs/user-guide.md`/`.en.md`（安装依赖）、README 技术栈与致谢段、仓库树注释。第一片（宿主装配切换）就改 `architecture.md` 与 README 栈段，其余随各自片；旧宿主 ADR `architecture/2026-08-20-ryn-shell-bundled-dsh-runtime` 的交叉链接或 Erratum 同批落。
- 待证项（G0/G1 实跑验收，不另设能力探针）：`av-webview` 在本仓页面里的消息契约细节（含 `invoke` 的 JSON 解析语义与失败面）、Linux 托盘菜单行为、无边框拖拽/缩放体验；Linux 双后端差异——WPE 优先（离屏合成）与 WebKitGTK 回退（原生子窗）在输入法、剪贴板、拖拽与 `TryGetPlatformHandle` 上不同，两种机器都要过一遍；`EnableDevTools` 是否在无 WPE 的 GTK 路径生效需实测（官方文档 WPE 节称该事件仅在 WPE 路径触发，与 GTK 示例矛盾）；`PermissionRequested` / `DownloadStarting` 三端默认差异与接管形状（G0 未接，附件下载/通知/媒体权限会静默失败或不受控弹窗）。
- **无边框顶栏不是桥的纯消费方**（2026-10-23 核实）：`Avalonia.Controls.Window.BeginMoveDrag` 只接受 `PointerPressedEventArgs`，且三后端都真正消费它（Win32 `e.Pointer.Capture/IsPrimary`、X11 从指针坐标发 `_NET_WM_MOVERESIZE`、Wayland 取 `e.PlatformInputEventCookie` 作 `xdg_toplevel.move` 的 serial）；页面区域的指针被原生 WebView 子窗消费，Avalonia 收不到按下事件。故自绘顶栏的拖拽/缩放必须补平台原生互操作（Win32 `WM_SYSCOMMAND`+`WM_LBUTTONUP` 或 `WM_NCLBUTTONDOWN`、X11 `_NET_WM_MOVERESIZE`），Wayland 无公开路径；macOS 可复用 `Avalonia.Native` 的分支（其 `BeginMoveDrag()` 显式回退到 `[NSApp currentEvent]`，正为 WebView 消费按下的场景而写）。顶栏片的估时因此不含「仅接线」。
- 迁移期与 0.6.14 并行存在两套宿主代码，评审与测试面按阶段切换，不长期双轨。
- G0 已落地（首片）：`Program` 入口切到 `Hosting/AvaloniaHost`，Avalonia 窗口 + `NativeWebView` 加载壳 origin，既有 `RuntimeStarter`/`HarnessRuntimeHost`/`DshLoopbackProxy` 原样复用；实跑取得 `dsh web` URL、铸币 303、就绪探活与页面 WS 升级（本机验证见 `.plan/scratch/g0-run2.log`）。`docs/architecture.md` 与 README 栈段/树注释/致谢同片更新。增量闸的 `packages ≤ 3` 一次性豁免见 [avalonia-g0-entry-switch](../../implemented/architecture/2026-10-22-avalonia-g0-entry-switch.md)。G1 首批已落地（托盘、关窗到托盘、二启唤回），见 [avalonia-g1-tray-recall](../../implemented/architecture/2026-10-22-avalonia-g1-tray-recall.md)；G1 其余片：失败面对话框已接启动失败路径（进程内模态，见 `implemented/architecture/2026-10-23-avalonia-failure-dialog.md`），无边框顶栏已接（三平台原生拖拽/缩放，Wayland 降级保留装饰，见 `implemented/architecture/2026-10-23-avalonia-caption-bar.md`），G1 其余片中的恢复页、以及 G3 的首启引导与崩溃监督已接（见 [avalonia-g1g3-recovery-bootstrap-supervision](../../implemented/architecture/2026-10-23-avalonia-g1g3-recovery-bootstrap-supervision.md)；阶段表里引导/首启归 G3，本句按该归属记录）。页面桥已落地：`window.__shell`（invoke/on/_emit，nonce+顶层 origin 双判据）、PagePump 注册端口、健康探针与外链拦截，见 [avalonia-g2-shell-bridge](../../implemented/architecture/2026-10-22-avalonia-g2-shell-bridge.md)。 G3 退出/重启已落地：`ExitPipeline` 在装配期构造（停运行时 → 释放监听器 → 关窗/拉起新实例 → 看门狗），托盘补「重启」项、`desktop.app.restart` 接回新宿主，见 [avalonia-g3-exit-restart](../../implemented/architecture/2026-10-23-avalonia-g3-exit-restart.md)；更新三命令与更新 UI 仍在后续片。

## Related

- [webview-shared-memory-rendering](../../implemented/architecture/2026-10-18-webview-shared-memory-rendering.md)：WebKitGTK 内存现状与 `LinuxRenderingMode` 的出处。
- [ryn-shell-bundled-dsh-runtime](../../implemented/architecture/2026-08-20-ryn-shell-bundled-dsh-runtime.md)：旧宿主选型；本笔记提出替换。
- [nativeaot-probe-after-composition-refactor](../../implemented/architecture/2026-09-03-nativeaot-probe-after-composition-refactor.md)：发运形态维持 JIT 的依据。
