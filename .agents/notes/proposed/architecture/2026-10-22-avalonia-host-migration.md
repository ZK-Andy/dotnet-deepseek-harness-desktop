# Agent Note: Avalonia 宿主迁移（Ryn → Avalonia 12.1.x）

Status: proposed

## Problem

宿主层 Ryn 0.38.0 已不再交付演进：上游 `Yupmoh/Ryn` 主分支最近提交与最新 release 均为 2026-09-13（v0.38.0），2026-10-19 的推送来自 feature/dependabot 分支而非 main；单维护者、28 star。Linux WebKitGTK 匿名内存增长（上游 issue #81）最后一条维护者回复停在 2026-08-30，本仓只能以 `LinuxRenderingMode.SharedMemory` 缓解（ADR `2026-10-18-webview-shared-memory-rendering`，n=1【探索性】）。

上游停滞后，任何绑定层增强（例如调用 `webkit_memory_pressure_settings_*` 给 WebProcess 设上限）都只能靠 fork 或自持原生绑定；宿主替换窗口事实上只剩「自持」或「换宿主」两条路。

本仓与 Ryn 的耦合面：主工程 44 个文件、Core 7 个、Infrastructure 6 个、测试 23 个文件；页面侧命令通道写死 `window.__ryn.invoke`（companion `client.js`、`CaptionBar.cs`、`wwwroot/index.html`）。

## Proposal

**决定：宿主层从 Ryn 迁到 Avalonia 12.1.x + `Avalonia.Controls.WebView` 12.1.1（MIT）。**

- TFM 维持 `net10.0`（Avalonia 12 的最低支持版本恰为 .NET 10，见官方支持矩阵）；发运形态维持 JIT self-contained，AOT 决策不变。
- 只换宿主与桥接：保留 dsh Web 客户端与 JS 插件生态；不转原生界面；不承诺内存下降。
- JS↔C# 走包自带 `av-webview` 消息桥（`WebMessageReceived` / `InvokeScript`），实现 `window.__shell` 替换 `window.__ryn`；`ICommandRouter` / `CommandFrame` 契约与命令 ID 不变，窗口命令（startDrag / toggleMaximize / close）走同一通道。
- `DSH_DEVTOOLS=1` 映射到 `EnvironmentRequested.EnableDevTools`，仅保留开发开关，不进 parity 范围。
- dsh 的 `inspector-profile` / `session-inspector` / `inspector` 系列是上游 Web 插件，随 dsh 页面运行，不属于壳迁移范围。

**证据（不再逐能力自测）**：`NativeWebView` 已在生产项目中使用——YoutubeDownloader（NativeWebView + cookie 管理 + `NavigationStarted` + devtools + 每引擎环境参数）、UniGetUI（WebView2 运行时检测与降级）、Libation / Pixeval / Everywhere（桌面发行）；`TrayIcon` 与 `ExtendClientAreaToDecorationsHint` 是 Avalonia 核心控件。控件 API 面包含 `NavigationStarted` / `NavigationCompleted` / `NewWindowRequested` / `WebMessageReceived` / `WebResourceRequested` / `EnvironmentRequested` / `TryGetCookieManager` / `InvokeScript` / `TryGetPlatformHandle`。

**阶段（按目标状态，约 7–8 人周）**

| 阶段 | 目标状态 | 估算 |
|---|---|---|
| G0 | 跑起来：Avalonia 窗口 → NativeWebView → 复用既有 spawn/代理 → dsh UI 可见 | 1–2 天 |
| G1 | 壳齐：托盘/关窗到托盘、单实例、无边框顶栏、失败面对话框、恢复页 | 1 周 |
| G2 | 桥通：`av-webview` 替换 `__ryn`，PagePump、健康探针、外链拦截 | 1.5 周 |
| G3 | 面齐：12 个命令路由、引导/首启、locale、更新 UI、诊断、companion | 2 周 |
| G4 | 能发：三平台打包/更新/CI/smoke，WebView2 运行时缺失检测 | 1.5–2 周 |
| G5 | 对齐回滚：0.6.14 parity、浸泡基线、0.7.0 | 1 周 |

## Alternatives considered

- **维持 Ryn（pin 0.38.0 或 fork）**：零迁移成本；但上游停止交付，内存与 WebKitGTK 新能力无解，fork 需自持 C# 绑定 + 原生归档工具链。落败。
- **自持平台 WebView 绑定（WebKitGTK / WebView2 / WKWebView P/Invoke）**：可控性最高，可直接调用 `webkit_memory_pressure_settings_*`；但等于重写一遍 Ryn 的绑定与插件面，成本与收益不匹配。落败。
- **打包 Chromium（CEF / DotNetBrowser）**：唯一能真正摆脱 WebKitGTK 的路径；但安装器 +150–250 MB、Chromium 安全跟版、.NET 跨平台绑定薄弱（CefSharp 仅 Windows、CefNet 停滞、DotNetBrowser 商业）。当前非必要，落败。
- **Tauri / Uno / MAUI**：Tauri 在 Linux 同为 WebKitGTK 且换语言栈；MAUI 无 Linux 支持；Uno 生态弱。落败。
- **直接转原生界面**：割裂 dsh 的 JS 客户端插件生态；且 Avalonia 本身可作为将来原生 UI 的底座，无需在本阶段决定。落败（本阶段）。
- **继续等待上游修复**：issue #81 自 2026-08-30 无进展、主分支停滞，等待无止损点。落败。

## Consequences

- 0.6.14（Ryn 宿主）保留为回滚产物；0.7.0 为 Avalonia 首发。
- 内存不承诺下降：Linux 仍是 WebKitGTK（Avalonia 可按环境走 WPE），dsh 进程树 636 MB 的实测（node 452 MB + codegraph 三进程 184 MB，n=1【探索性】）含 codegraph 三进程；该组件不在当前环境，184 MB 部分不可复用，node 的 452 MB 仍只有 n=1。
- Windows 依赖 WebView2 运行时：复用现有「缺时提示自装」路径，并按 UniGetUI 的实机教训补检测（缺失时回退旧 EdgeHTML 会在 `async void` 续体里崩进程）。
- 打包面变化：`verify-package-layout.sh` 的 `libsaucer*` / `libryn-pty` 断言改为 Avalonia 原生库清单；更新 feed 与三平台 smoke 同步修改。
- 文档需在迁移完成前保持旧口径、迁移后同步改写：`docs/architecture.md`（技术栈与启动链）、`docs/development.md`（构建与 `DSH_DEVTOOLS`）、`docs/testing.md`（打包校验清单）、`docs/cookbook.md`（Ryn/WebKitGTK 条目）、README 技术栈段。旧宿主 ADR `architecture/2026-08-20-ryn-shell-bundled-dsh-runtime` 需要交叉链接或 Erratum。
- 待证项（G0/G1 实跑验收，不另设能力探针）：`av-webview` 在本仓页面里的消息契约细节、Linux 托盘菜单行为、无边框拖拽/缩放体验。
- 迁移期与 0.6.14 并行存在两套宿主代码，评审与测试面按阶段切换，不长期双轨。

## Related

- [webview-shared-memory-rendering](../../implemented/architecture/2026-10-18-webview-shared-memory-rendering.md)：WebKitGTK 内存现状与 `LinuxRenderingMode` 的出处。
- [ryn-shell-bundled-dsh-runtime](../../implemented/architecture/2026-08-20-ryn-shell-bundled-dsh-runtime.md)：旧宿主选型；本笔记提出替换。
- [nativeaot-probe-after-composition-refactor](../../implemented/architecture/2026-09-03-nativeaot-probe-after-composition-refactor.md)：发运形态维持 JIT 的依据。
