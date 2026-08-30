# Agent Note: external-link-opener-failure-toast

Status: implemented

## Problem

导航层外部链接拦截（`Services/RynNavigationCallbacks`，Ryn.Callbacks `WebViewNavigating`）在 `_opener(url)` 打开系统浏览器**失败**时（`xdg-open`/`Process.Start` 没默认浏览器或报错），当前只写一条 host.log，随后照样 `return NavigationDecision.Block`——站外链接本就不该把 WebView 导航走，但用户侧看到的是**静默死链**：点了外链，浏览器没弹、页面也不动，毫无提示。`fail loud` 只对日志（开发者）loud，对**用户**不 loud（R2 代码审查 N2）。

## Decision

给外部链接打开失败补一个**页面级 toast 提示**，让用户知道链接没打开、可手动复制地址到浏览器：

1. **宿主侧**（`Services/RynNavigationCallbacks`）：构造新增可注入的 `Action<string>? notifyLinkFail`（携带失败的 URL）。`OnWebViewNavigating` 里 opener 返回 false **或**抛异常即视为打开失败 → `notifyLinkFail?.Invoke(url)`。委托注入保持本类可单测（对齐 `DesktopTrayCommandRouter._notify` 的模式）。
2. **DesktopBootstrap.RegisterServices 接线**：工厂注册 `RynNavigationCallbacks` 时，`notifyLinkFail` 接 `sp.GetRequiredService<IRynWebView>().EmitEvent("desktop.externalLinkOpenerFailed", new ExternalLinkOpenerFailedFrame(url), AppJsonContext.Default.ExternalLinkOpenerFailedFrame)`——把失败经 deferred `IRynWebView` 推给页面（导航回调触发时页面必然已加载，deferred 转发到真实窗口）。
3. **AOT 序列化通道**：`Services/AppJson.cs` 新增 `internal sealed record ExternalLinkOpenerFailedFrame(string Url)` 并在 `AppJsonContext` 注册（源生成，漏注册编译期即失败）。
4. **companion 侧**（`plugins/dsh-desktop-companion/client/client.js`）：`apply(ctx)` 里订阅 `window.__ryn.on('desktop.externalLinkOpenerFailed', ...)`，命中时用纯 DOM 创建一个 `#ddc-linkfail-toast` 浮层（不经 dsh slot 树——它是页面级浮动层），显示标题+URL（无 URL 退化为正文），5s 后淡出。文案进现有 `zh`/`en` 字典（`linkFailTitle`/`linkFailBody`），经 `ctx.locale.bind('desktop-companion')` 随语言切换。companion version `0.0.15 → 0.0.16`（功能变更，随包闭包版本感知升级）。
5. 打开成功时**不**通知（正常路径零打扰）。

## Alternatives considered

- **仅加 host.log 留痕（维持现状）**：`fail loud` 只对日志生效，用户侧仍是静默死链——正是 R2 N2 指出的瑕疵。**落败**。
- **系统托盘通知（复用 `DesktopTrayCommandRouter._notify`）**：走系统通知，但外链打开失败是**页面内交互**的即时反馈，用系统级通知显得重、且与"点击即知"的语义不符；且托盘通知不总是可达（无托盘会话禁用）。**落败**。
- **companion 用 React 渲染 toast（复用 slot 系统）**：toast 是页面级浮动覆盖层，不属于任何 slot 区域（sidebar/settings）；借 React 树反而增加耦合。**落败**——纯 DOM 浮层最贴合"瞬时浮动提示"。
- **opener 失败时不 Block，让 WebView 导航走**：放弃"站外链接不带走页面"的核心安全语义，只为"用户能看到页面变化"。**落败**——导航层拦截的目的就是防止 WebView 被带到外部站，不能为提示而放弃。

## Consequences

- 用户点外部链接且系统浏览器打开失败时，页面出现 toast 提示（标题+URL），5s 消失；打开成功零打扰。
- 新增事件 `desktop.externalLinkOpenerFailed`（host→companion 方向，经 Ryn `EmitEvent`）；payload 类型 `ExternalLinkOpenerFailedFrame` 进 AOT 序列化通道。
- companion `client.js` 新增 toast 订阅 + 纯 DOM 渲染；version 0.0.16 随闭包版本感知升级。
- `fail loud` 对用户侧生效：不再静默失败，但也**不弹浏览器**（保持"站外链接不导航 WebView"）。
- 测试：`RynNavigationCallbacksTests` 新增 opener 返回 false / 抛异常 / 成功三态的 notify 断言（行为级回归）。

## Related

- [ryn-navigation-callbacks](../feature/2026-08-28-ryn-navigation-callbacks.md)：导航层外部链接拦截的 ADR，本变更在其失败路径补用户提示。
- [split-program-main-god-function](../architecture/2026-08-30-split-program-main-god-function.md)：本 ADR 的接线随 P0 拆 Main 迁至 `DesktopBootstrap`。
- R2 代码审查 N2（外部链接打开失败补 toast 提示）。
