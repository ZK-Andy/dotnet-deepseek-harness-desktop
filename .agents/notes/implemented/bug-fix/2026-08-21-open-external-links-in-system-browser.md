# Agent Note: open-external-links-in-system-browser

Status: implemented

## Problem

桌面壳（Ryn WebView + 托管 `dsh --profile web`）内，点击**外部链接**打不开。前端把 URL 型内容渲染成 `<a href="..." target="_blank" rel="noopener noreferrer">`（`@deepseek-ai/dsh-web-frontend` 的渲染器如此生成）。在浏览器/web 端，`target="_blank"` 会新开标签页，正常；但在 Ryn 的 WebView 壳里，它触发的是 saucer 的**新窗口请求**（底层符号 `saucer_navigation_new_window`）——而 Ryn 0.30 **不向外暴露导航/新窗口事件**（反编译 `Ryn.Core.dll`：全部 `saucer_webview_on` 只注册了 `SAUCER_WEBVIEW_EVENT_DOM_READY`（事件 2），`_webview` 句柄私有，外部无法自行 `saucer_webview_on` 注册导航回调）。于是该新窗口请求被静默丢弃 → 用户点了没反应。真实场景：AnySearch 搜索返回的 `x.com/...` 等结果链接全部打不开。

## Decision

在**宿主壳**一侧打开外部链接，逻辑在 C#，不碰前端。借助 Ryn 官方扩展点——**向每个页面注入捕捉脚本 + 自定义命令 router**（与 Ryn 内置 FileDrop/TitleBar/ConsoleForward 同模式）：

- **注入点击拦截脚本**（`Services/ExternalLinkClickCatcher.cs`）：`IRynWebView.InjectScriptAsync` 以 READY 注入（覆盖当前及后续每页/崩溃重启后新页），并用 `EvaluateJavaScriptAsync` 对当前已加载页补跑一次。脚本用 capture 阶段 `document.addEventListener('click', …, true)`：命中 `a[href]` 且为**站外 `http(s)`**（非同源）或带 `target="_blank"` 时 `preventDefault()`，再 `window.__ryn.invoke('app.openExternal', { url })` 交给宿主；同源 SPA 导航、`ryn://`、`data:`、`mailto:` 等一律放行。含 window 级幂等标记，避免重复监听。
- **宿主命令路由**（`Services/ExternalLinkCommandRouter.cs`）：`Ryn.Ipc.ICommandRouter`，`CanRoute` 命中 `app.openExternal`，`RouteAsync` 解析 JSON 载荷（`{ url }`）、用 `ExternalLinkPolicy` 二次校验（仅绝对 http/https)、`Process.Start(UseShellExecute=true)` 交给系统默认浏览器；失败记日志不向 JS 抛错（返回成功帧避免 JS 侧 `catch` 噪声）。打开器经委托注入，测试用假开器避免真弹浏览器。
- **纯策略**（`Services/ExternalLinkPolicy.cs`）：判定 href 是否应外部打开（绝对 http/https + 非同源或无可参照来源 + 默认端口等价对齐）。
- **接线**（`Program.cs`）：`ConfigureServices` 注册 `services.AddSingleton<ICommandRouter, ExternalLinkCommandRouter>()`（`RynCommandDispatcher` 自动收集）；build 后后台任务等 `app.WebView` 可达时做一次持久注入 + 当前页补跑（最多重试 60×500ms，不阻塞首启）。

## Alternatives considered

- **宿主捕获 saucer 新窗口事件（方案 A 字面）**：反编译实证 Ryn 0.30 不暴露导航/新窗口事件、句柄私有，外部无从 `saucer_webview_on(…, EVENT_NAVIGATE, …)` 注册回调（枚举里有 `SAUCER_WEBVIEW_EVENT_NAVIGATE`，但 Ryn 未转发、句柄私有）。落败：当前 Ryn 版本 API 面不可达。
- **改前端渲染**：给 `_blank` 链接换非 `_blank` 或加宿主调用——落败：产物在前端 bundle 里，桌面壳应保持"宿主负责系统集成"，改前端会让 web/桌面行为分叉、且随前端升级易失。
- **自定义 scheme（如 `open-in-browser://`）拦截**：落败：需前端配合把 `href` 改 scheme，改动泄漏进前端，且丢 `noopener` 语义。
- **仅 `window.onload` 后一次性 `EvaluateJavaScriptAsync`**：落败：SPA 长会话/页面重渲染后监听器易失效；`InjectScriptAsync` 创建期注入更接近"每页都有"的可靠语义。

## Consequences

- 收益：桌面端点击外部链接（AnySearch 结果、普通 URL 等）在系统默认浏览器打开；SPA 内部导航、非 http(s) scheme 不受影响；逻辑全在宿主、可单测。
- 代价/风险：`UseShellExecute=true` 依赖系统默认浏览器/`xdg-open`，无默认浏览器时 `Process.Start` 抛异常——已 catch 并记日志（fail loud，不静默）。注入脚本与前端自身对链接的处理可能在 capture 阶段优先（职责归宿主，符合预期）。
- 验证：`dotnet build` 0 警告 0 错误；`dotnet test 56/56`（新增 `ExternalLinkPolicyTests` + `ExternalLinkCommandRouterTests`）；门禁 `verify-adr-format` / `verify-doc-budgets` / `verify-md-links` 全绿。真实桌面点击需重启应用验（沙箱渲染受限未复跑）。

## Superseded by

- [ryn-navigation-callbacks](../feature/2026-08-28-ryn-navigation-callbacks.md)：Ryn 0.32.0 起把外部链接处理从本文的点击层注入脚本迁到宿主导航层（`Ryn.Callbacks` 的 `WebViewNavigating/Navigated`）。本文保留的 `ExternalLinkPolicy`/`ExternalLinkCommandRouter`（`app.openExternal`）两个组件仍存活——导航层拦截后经共享 `SystemBrowser` 打开，router 保留给已发布旧版 companion 与 Ryn 命令面。本文记录的反编译实证（Ryn 0.30 不暴露导航事件）是导航回调方案成立的关键约束背景（Ryn <0.32 无法在导航边界拦截）。
