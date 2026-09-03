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

本仓库**不改代码**，向上游报告并等治本；本 ADR 仅作决策记录，不含任何变更：

- 已提交 saucer/saucer **issue #90**（"Right-click 'Open Link' is a no-op on WebKitGTK (no 'create' signal handler)"，2026-09-01）：saucer WebKitGTK 后端补 `WebKitWebView::create` 信号，把右键「在新窗口打开链接」转成 `navigate`（`new_window=true`）事件（或按 saucer 设计另行承接新 WebView）。
- 本仓库侧修复依赖上游：待 saucer 修复 + Ryn bump + 本仓库 Ryn 升级后，右键即走与左键一致的导航拦截路径。
- **明确排除（2026-09-02 拍板）**：不采用"禁用/关闭 WebView 右键菜单"或前端注入拦截作为缓解——见 Alternatives「禁用/裁剪右键菜单」与「注入 JS 自绘/拦截右键菜单」。

### 上游动态（2026-09-02 更新）

saucer 维护者 Curve 已回复 issue #90（2026-09-02，issue 标题随之改为 "[Feature] WebView `create` event"）：

- **"intended"的准确含义**：Curve 指当前行为对 saucer 设计是**有意为之**——「Open Link」绕过 policy 决策、直接跳"创建新窗口"；`navigate` 事件定位是"可拦截的导航"，不是"新建窗口"通道。此判断属 saucer 层，**非本产品决策**。
- 正确做法（Curve 认同方向）：把职责**拆成两个事件**（导航 vs 新窗口创建），但需保证跨后端（WebKitGTK/WebView2/WKWebView）语义一致。
- **额外阻塞**：WebKitGTK 期望 create 时返回新建 webview 指针并**接管其所有权**——Curve 认为须先解决 saucer **issue #53**（"Integrating into existing windows"）才能安全实现。故上游修复时间表不可控，本仓库不作为依据。
- issue #90 当前 **OPEN**（未关、未拒、无时间表）。

### 为何本仓库不"禁用/关闭右键菜单"（2026-09-02 记录）

| 备选 | 结论 |
|---|---|
| saucer `set_context_menu(false)` 整体关 | 不可达（Ryn 未透出，见 Alternatives） |
| 前端注入关/改菜单 | 不可行（见 Alternatives） |
| **维持现状 + 仅记录**（左键可用、右键两入口 Linux 上无效） | **采纳**：右键菜单里复制/粘贴/全选/检查是 dsh 桌面壳刚需，为一个死入口整体杀掉是负收益；现状仅 Linux 右键两入口静默无效，左键外链链路完好。 |

## Alternatives considered

- **禁用/关闭右键菜单（2026-09-02 评估，排除）**：saucer 有整体布尔开关（`include/saucer/webview.hpp`：`context_menu()` / `set_context_menu(bool)`），关掉是**整个菜单消失**、无 per-item 裁剪；且 Ryn 0.35.1 **未透出**该开关——反射 `IRynWebView`/`RynOptions` 无 context-menu/new-window 成员，所需 `saucer_webview*` 指针在 `RynWebView._webview`/`RynWindow._webview` **private 字段**（须反射 + unsafe 戳原生指针，私有 API 依赖，违 R3、Ryn 升级即碎）。即使打通，右键菜单里的复制/粘贴/全选/检查（壳内刚需）一并陪葬。**排除**——不可达 + 负收益。
- **前端注入 JS 自绘/拦截右键菜单（备选，维持排除）**：接管 `contextmenu` 事件命中站外 http(s) 时 `preventDefault` + invoke `app.openExternal`。能立即缓解右键外链，但：①侵入 dsh web 页面渲染面；②依赖 `ExternalLinkCommandRouter` 路径；③与导航层拦截两套逻辑重复；④与系统原生菜单惯例/页面样式冲突，复制粘贴也连坐。**落败**（2026-09-02 复核仍排除——连"裁剪右键菜单项"都做不到，注入只能整段替换或整段禁用，代价不变）。
- **只向上游提 issue / PR（采纳方向）**：治本、不侵入本仓库与第三方页面；但依赖 saucer 发版与 Ryn bump 节奏，期间右键菜单"打开链接"仍无反应（用户可左键代替）。saucer 贡献规范明确要求"先沟通再写补丁"，故先提 bug issue 而非直接提 C++ PR。
- **本仓库改 `RynNavigationCallbacks` 处理 `IsNewWindow`（备选）**：当前代码对 `newWindow=True` 的站外链接已能拦截（左键实证）；但右键根本不触发回调，故改回调**救不了右键**。**落败**——改动无效，问题不在回调层。
- **不做（基线）**：保留现状（左键正常、右键无反应）。**落败**（2026-09-01 原判）——用户明确反馈右键是可用性缺陷；但 2026-09-02 复核后"不做任何变更、仅记录 + 待上游"成为**当前采纳态**（非备选落败），因禁用菜单与注入两路缓解均排除，且上游已确认方向与阻塞。

## Acceptance criteria

- 上游 saucer 修复（issue #90/#53 推进）后：真机桌面壳 `dsh web:` 页面**右键外链「打开链接」能打开系统浏览器**（与左键一致的拦截 + `SystemBrowser.Open` 成功）。
- 期间（等上游）：本仓库 `RynNavigationCallbacks`/`SystemBrowser` 保持现状（已实证正确），**不做**右键菜单禁用/注入等缓解（2026-09-02 拍板：仅记录，不做任何变更）。

## Risks

- 上游 saucer 修复节奏不可控（Curve 明示须先解 #53，无时间表）；若久拖，用户在 Linux 上持续遇到右键无反应（左键可用）。
- 若后续上游把事件拆成"导航 vs 新窗口"两事件，本仓库导航回调消费面需随之适配（届时评估）。
- 本 ADR 中「根因 = 缺 create 信号」为【推断 · 已由源码 + WebKit 行为佐证】，非 saucer 官方确认；Curve 回复已把机制归为「Open Link 绕过 policy 直入创建窗口」——与 create 信号缺口一致，若官方后续给出不同机制，以官方为准。
- 若未来 Ryn 透出 saucer context-menu 开关/new-window 事件，本记录中的"不可达"前提失效——届时需重新评估（这正是本 ADR 保留在 proposed 而非 rejected/archived 的原因）。

## Related

- [ryn-navigation-callbacks](../../implemented/feature/2026-08-28-ryn-navigation-callbacks.md)：导航层外链拦截的 ADR；本次右键缺陷发生在该层之下（saucer 回调未被触发）。
- [open-external-links-in-system-browser](../../implemented/bug-fix/2026-08-21-open-external-links-in-system-browser.md)：外部链接交系统浏览器的原始 ADR；`SystemBrowser.Open` 行为经本次实证正确。
- 上游 saucer/saucer issue #90（2026-09-01 提交，2026-09-02 Curve 回复确认方向 + 依赖 #53）。
- 上游 saucer/saucer issue #53（"Integrating into existing windows"，Curve 明示为 #90 前置）。
