# Agent Note: ryn-navigation-callbacks

Status: implemented

## Problem

外部链接处理一直是**点击层 hack**，不覆盖导航层。companion `client.js`（`__ryn_externalLinkCatcher`）用 capture 阶段 `document.addEventListener('click', …, true)` 拦截 `a[href]` 点击、命中站外/`_blank` 的 http(s) 时 `preventDefault()` + `window.__ryn.invoke('app.openExternal')`。这只兜得住**用户点击 `<a>`** 这一条路径——`window.location = external`、`window.open()`、`<form>` 提交、`<meta http-equiv=refresh>` 这些**不经过点击的整页导航**，会直接把 dsh 页面替换成外部站（用户被"带走"，不可逆），现行机制完全覆盖不到。

根因（2026-08-21 反编译实证）：Ryn 0.30 不暴露 saucer 导航/新窗口事件（只注册 `SAUCER_WEBVIEW_EVENT_DOM_READY`、`_webview` 句柄私有），故壳内无法在导航边界统一拦截——只能退而求其次在前端捡点击。

联动缺口：
- `RuntimeSupervisor.onNavigated`（`startupNavigationSettled`）现由 `_navigate(url)`（`NavigateAsync` 返回）后立即触发——**"拿到导航任务" ≠ "内容已到达"**，横幅门控缺一个权威的"页面已到达"信号。

## Decision

利用 Ryn 0.32.0 新引入的 **`Ryn.Callbacks`** 包（源生成 + NativeAOT-safe 导航回调），把外部链接处理**从"点击层 hack"迁到"导航层正规军"**，并加固崩溃恢复/横幅门控的"页面到达"信号。用户拍板：**精简方案**——只用导航层拦截，删除 companion 点击拦截；**并接入导航回调到恢复/横幅门控**。

### 1. 依赖升级

`src/DeepSeek.Harness.Desktop/DeepSeek.Harness.Desktop.csproj`：Ryn 三包 `0.30.5 → 0.32.0`，新增 `Ryn.Callbacks`（自带源生成器 `analyzers/dotnet/cs/Ryn.Callbacks.Generator.dll`，一个 PackageReference 即够，无需单独 analyzer 引用）。`Ryn.Ipc.Generator` 保持作为 analyzer 引用不变。

### 2. 导航层外部链接拦截（替换 companion 点击拦截）

新增 `Services/RynNavigationCallbacks.cs`，宿主侧 `[RynCallback]` 处理器：

- `[RynCallback(WebViewNavigating)]` 返回 `NavigationDecision`。先看 `context.IsUserInitiated`：宿主程序化导航（崩溃恢复 `NavigateAsync`、SPA 内部重定向）为 false 一律放行——否则恢复流程会被本回调误拦。仅对用户发起的导航，用 `ExternalLinkPolicy.IsExternalHttpLink(context.Url, currentOrigin: _currentOrigin, out _)` 判定：非同源绝对 http(s) → `NavigationDecision.Block` + 经共享 `SystemBrowser` 打开；同源 SPA / `ryn://` / `data:` / 非 http(s) → `NavigationDecision.Allow`。
  - `currentOrigin` 初始 = `Program.cs` `webUrl` 的 origin（dsh 页面 URL），并在每次 `WebViewNavigated` 刷新为实际到达 URL 的 origin——崩溃恢复可能把端口漂移（ADR child-process-reaping-port-drift），冻结首启 origin 会让漂移后的同源 SPA 路由被误判为外部。`ExternalLinkPolicy` 在 `currentOrigin: null` 时保守地把一切绝对 http(s) 视为外部。
- `[RynCallback(WebViewNavigated)]` 刷新 `currentOrigin`、记录导航（`context.Url`）留痕，并回调"导航已到达"信号。
- 打开器抽共享 `Services/SystemBrowser.cs` 静态 `Open(url)`（Linux `xdg-open` + 重定向输出，其余 `Process.Start(UseShellExecute=true)`）；`ExternalLinkCommandRouter` 与 `RynNavigationCallbacks` 的默认打开器收敛到它，消除两份复制逻辑。
- `ConfigureServices` 加 `services.AddRynCallbacks();` + `services.AddRynNavigationCallbacks();`（源生成，`Ryn.Callbacks` 命名空间），注册 `IRynCallbackRouter` 单例；再用工厂覆盖源生成的 handler 无参注册（导航回调依赖：opener/log/currentOrigin 初始值经工厂注入）。Ryn 经 `SetWebViewNavigatingHandler` 把它挂到窗口。

### 3. 删除 companion 点击拦截

`plugins/dsh-desktop-companion/client/client.js` 删除外链接管整段（`__ryn_externalLinkCatcher` flag、`isExternal`、`onClick` capture 监听、`window.__dshDesktopCompanionLinks` guard），以及文档头注释里的相关描述。companion version bump（功能变更语义，随包闭包版本感知升级）。`app.openExternal` 命令与 `ExternalLinkCommandRouter` **仍保留**——导航层拦截后经共享 `SystemBrowser` 直开（不经命令路径），router 保留给**已发布旧版 companion**（其 profile 副本仍通过 `app.openExternal` 触达）与 Ryn 命令面兼容。

### 4. 导航到达信号加固

`Program.cs` 的 `startupNavigationSettled` 从 `supervisor.onNavigated`（`NavigateAsync` 返回即触发）改由 `WebViewNavigated` 回调触发（`RynNavigationCallbacks.SetOnNavigated` 注入一个 `Action` 到该 TCS）。横幅门控由此获得"页面已到达"的权威信号；`RuntimeSupervisor.onNavigated` 参数随之删除（已无调用方，清理 dead path）。

## Alternatives considered

- **仅导航层拦截，删 companion 点击拦截（采纳，用户拍板"精简"）**：架构最干净——外部链接处理收敛到宿主导航边界一处，消除"导航层 vs 点击层"两套逻辑与 `__ryn_externalLinkCatcher` 过渡 flag 舞。代价：需改 companion（删捕获段 + bump version），且必须实测导航回调在 `_blank`/`window.open` 等场景都拦得住。
- **导航层拦截 + 保留 companion 点击拦截**：导航层兜底"非点击"漏洞、点击层维持现状。两层共存需处理重复拦截去重（companion 已 `preventDefault` 则不走到导航层），实现更稳但改动面更大、逻辑重复。**落败**（相对用户拍板）。
- **仅加导航回调日志留痕，不拦截**：零行为变更、风险最低，但没真正堵住非点击导航漏洞。**落败**（未达成"堵漏洞"目标）。
- **宿主捕获 saucer 新窗口事件（2026-08-21 方案 A 字面）**：反编译实证 Ryn <0.32 不暴露导航事件、句柄私有。**落败**——API 面不可达；本变更正是等 0.32.0 暴露后才成立。
- **不接 `WebViewNavigated` 到横幅门控，维持现有 `onNavigated`**：改动面收敛，但"页面到达"信号不权威（`NavigateAsync` 返回 ≠ 内容到达）。**落败**（用户拍板接入加固）。
- **拦截一切导航（含宿主导航），`currentOrigin` 冻结首启 origin**：简化、"覆盖一切导航"。**落败**——三路评审 R2 指出两处回归：①崩溃恢复宿主 `_navigate(newUrl)` 若端口漂移（ADR child-process-reaping-port-drift），恢复导航本身会被当"方外"拦 Block → 页面停在恢复占位页；②漂移后同源 SPA 路由被误判外部。修正为**只拦 `IsUserInitiated`（用户主动导航）** + `currentOrigin` 随 `WebViewNavigated` 刷新。

## Consequences

- 外部链接处理收敛到宿主导航边界：用户非任何方式把 WebView 导航到外部 http(s) 都会被拦下并交系统浏览器；同源 SPA 路由、非 http(s) scheme 放行。修复 2026-08-21 方案未覆盖的非点击导航漏洞。
- companion `client.js` 删除点击捕获段：`__ryn_externalLinkCatcher`/`__dshDesktopCompanionLinks` 过渡 flag 退役；companion version 提升，闭包签名变化随 v0.3.11 重建并版本感知升级。
- `RuntimeSupervisor.onNavigated` 参数删除（已无调用方，`startupNavigationSettled` 改由 `WebViewNavigated` 触发）——恢复/横幅门控拿到"内容已到达"的权威信号，监督器只负责重启 + 导航，不再承载导航到达职责。
- 依赖升级伴随 Ryn 0.30.5→0.32.0 的 API 面变化（`Ryn.Callbacks` 新包 + 导航回调挂接）；需确认无与本仓使用面冲突的破坏性变更（2026-08-27 上游调研：dsh web: URL 鉴权除外，本次不涉 dsh bump）。
- `ryn.json` capability 面不变（`desktop` 已在白名单），导航回调走宿主 C# 侧不新增 IPC capability。

## Related

- [open-external-links-in-system-browser](../bug-fix/2026-08-21-open-external-links-in-system-browser.md)：点击层拦截的原始 ADR，本变更把其外部链接处理迁到导航层。
- [shell-firstboot-hardening](../bug-fix/2026-08-24-shell-firstboot-hardening.md)：`startupNavigationSettled` 横幅门控的出处，本变更改接 `WebViewNavigated`。
- Ryn 0.32.0 上游 `Ryn.Callbacks` 源码（v0.32.0 tag）：`IRynCallbackRouter`/`RynCallbackAttribute`/`RynCallbackKind`/`NavigationDecision`/`WebViewNavigatingContext`/`WebViewNavigatedContext`。
- Ryn 0.32.0 bump 上游调研见 HANDOFF 顶部滚动窗（2026-08-27）。
