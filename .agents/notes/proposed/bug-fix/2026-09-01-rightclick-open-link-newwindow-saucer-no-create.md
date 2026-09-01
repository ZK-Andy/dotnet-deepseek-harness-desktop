# Agent Note: rightclick-open-link-newwindow-saucer-no-create（右键"打开链接"无反应——saucer WebKitGTK 缺 create 信号承接）

Status: proposed

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

桌面壳（Ryn + WebKitGTK，`dsh web:` 页面）里**鼠标右键点击链接 → 选「打开链接」无任何反应**（不弹菜单后的导航、不打开系统浏览器）；同一链接**左键点击正常**（因链接带 `target="_blank"`，宿主拦截并交系统浏览器）。

**实证（真机带诊断日志，2026-09-01）**：在 `RynNavigationCallbacks.OnWebViewNavigating` 加导航分支日志、`SystemBrowser.Open` 加 `xdg-open` 进程日志后，真实桌面（Fedora 44 / WebKitGTK 4.1）dev 版复现：

- **左键** `target="_blank"` 外链 → `OnWebViewNavigating` 收到 `newWindow=True, userInitiated=True` → 分支③ 拦截 → `xdg-open` 启动且退出 `code=0` → 系统浏览器正常弹出。
- **右键**同一链接「打开链接」→ **日志无任何痕迹**：既无 `OnWebViewNavigating` 调用（无 `[nav]` 拦截/导航上下文），也无 `SystemBrowser.Open` 调用（无 `[browser-diag]`）。即**右键动作根本不触发宿主导航回调**。

**根因【推断 · 已由源码与 WebKit 行为佐证】**：saucer 的 WebKitGTK 后端**未实现 `WebKitWebView::create` 信号**。

- `src/wkg.webview.cpp`（v8.0.5 与 HEAD 一致）只 connect 了 `context-menu` / `load-changed` / `script-message-received` / `pressed` / `unpaired-release`，**无 `create`**。
- WebKitGTK 右键「在新窗口打开链接」（对 `target="_blank"` 链接）走 **`WebKitWebView::create`**——宿主须提供一个关联的新 `WebKitWebView`。saucer 无 `create` handler → WebKit 丢弃请求、不导航 → 静默无反应。
- 左键 `target="_blank"` 链接走 `decide-policy` 的 `NEW_WINDOW_ACTION`，saucer **能捕获**为 `new_window=true` 的 `navigate` 事件 → 宿主拦截成功。
- `on_context`（`src/wkg.webview.impl.cpp`）只 `return !context_menu`（开关默认菜单），不处理菜单项导航动作。

**为什么导航逻辑本身无 bug**：`RynNavigationCallbacks` / `ExternalLinkPolicy` / `SystemBrowser` 对左键的判定与打开全部正确（左键成功弹浏览器）；右键根因在更底层（saucer/WebKit），宿主回调压根未被触发。

## Proposal

本仓库**暂不改代码**，向上游报告并等治本：

- 已提交 saucer/saucer **issue #90**（"Right-click 'Open Link' is a no-op on WebKitGTK (no 'create' signal handler)"，2026-09-01）：saucer WebKitGTK 后端补 `WebKitWebView::create` 信号，把右键「在新窗口打开链接」转成 `navigate`（`new_window=true`）事件（或按 saucer 设计另行承接新 WebView）。
- 本仓库侧修复依赖上游：待 saucer 修复 + Ryn bump + 本仓库 Ryn 升级后，右键即走与左键一致的导航拦截路径。
- **可选（未拍板）**：若等不及上游，可在本仓库做防御——页面侧注入 JS 拦截右键 `contextmenu` 事件，把「打开链接」重定向到 `app.openExternal` 命令（侵入 dsh web 页面，体验与原生不一致，风险见 Alternatives）。

## Alternatives considered

- **只向上游提 issue / PR（采纳方向）**：治本、不侵入本仓库与第三方页面；但依赖 saucer 发版与 Ryn bump 节奏，期间右键菜单"打开链接"仍无反应（用户可左键代替）。saucer 贡献规范明确要求"先沟通再写补丁"，故先提 bug issue 而非直接提 C++ PR。
- **本仓库注入 JS 自绘/拦截右键菜单（备选）**：接管 `contextmenu` 事件，命中站外 http(s) 时 `preventDefault` + `window.__ryn.invoke('app.openExternal')`。能立即缓解右键外链，但：①侵入 dsh web 页面渲染面；②需在 `ExternalLinkCommandRouter` 路径已存在；③与导航层拦截形成两套逻辑（重复）；④右键"在新窗口打开"语义需精确判定，易漏站内链接。**落败**——违 "无意义侵入第三方页面 + 逻辑重复"，除非上游长期不修。
- **本仓库改 `RynNavigationCallbacks` 处理 `IsNewWindow`（备选）**：当前代码对 `newWindow=True` 的站外链接已能拦截（左键实证）；但右键根本不触发回调，故改回调**救不了右键**。**落败**——改动无效，问题不在回调层。
- **不做（基线）**：保留现状（左键正常、右键无反应）。**落败**——用户明确反馈右键是可用性缺陷。

## Acceptance criteria

- 上游 saucer 修复（或本仓库防御）后：真机桌面壳 `dsh web:` 页面**右键外链「打开链接」能打开系统浏览器**（与左键一致的拦截 + `SystemBrowser.Open` 成功）。
- 期间（等上游）：本仓库 `RynNavigationCallbacks`/`SystemBrowser` 保持现状（已实证正确），不引入侵入性改动。

## Risks

- 上游 saucer 修复节奏不可控；若久拖，用户在 Linux 上持续遇到右键无反应（左键可用）。
- 若走 JS 防御方案，可能影响后续上游修复后的双路径叠加（需去重）。
- 本 ADR 中「根因 = 缺 create 信号」为【推断 · 已由源码 + WebKit 行为佐证】，非 saucer 官方确认；上游 issue #90 已如实标注，若 saucer 维护者给出不同机制，以官方为准。

## Related

- [ryn-navigation-callbacks](../../implemented/feature/2026-08-28-ryn-navigation-callbacks.md)：导航层外链拦截的 ADR；本次右键缺陷发生在该层之下（saucer 回调未被触发）。
- [open-external-links-in-system-browser](../../implemented/bug-fix/2026-08-21-open-external-links-in-system-browser.md)：外部链接交系统浏览器的原始 ADR；`SystemBrowser.Open` 行为经本次实证正确。
- 上游 saucer/saucer issue #90（2026-09-01，已提交）。
