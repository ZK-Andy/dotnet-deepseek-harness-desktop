# Agent Note: 三端统一官方 macOS 形态的无边框窗口

Status: implemented

Review: FULL/2026-10-07#3/R1=ok R2=ok R3=ok

## Problem

壳窗口默认是平台原生标题栏，与官方 DeepSeek 桌面端的无边框观感割裂：标题条占纵向空间、观感随平台各自为政。需求方要求三端统一官方 macOS 截图呈现的形态：内容满幅到顶、无独立标题带、红绿灯悬浮于侧栏顶区。Ryn 0.38.0 内建完整 chrome 控制面（`TitleBarStyle`、自动拖拽条、`data-webview-*` 声明式窗口控制），具备零自研底座的对齐条件。

## Decision

**关键发现：dsh web 端内建完整的桌面 chrome 感知，全部挂在 `html[data-platform='darwin']` 上**（官方 Electron preload 独家设置；上游 ADR 2026-09-13-macos-hidden-titlebar-vibrancy）——侧栏 52px 红绿灯让位顶条、拖拽区标记、满幅布局、折叠全隐、右栏避让等都是 web 端现成规则。壳侧不需要自绘任何呈现面，把标记喂给页面即可激活官方 macOS 形态：

- **三端统一注入 darwin 标记**（`PageBridge.CaptionBar.Build` 前段）：`document.documentElement.dataset.platform='darwin'`，随同把 `body` 底色压回 `--dsw-alias-bg-base`——dsh 的 darwin 呈现含「html/body 透明 + 侧栏半透 tint」毛玻璃链，无 vibrancy 的普通窗口上会透到 webview 底色，扁平化后与浏览器渲染一致（vibrancy 留作后续）；扁平化 style 带查重守卫（双路注入防重复追加）。
- **窗口控制三端同为注入红绿灯三点**（`CaptionBar.Build`）：12px 系统色圆点（`#ff5f57/#febc2e/#28c840`）悬浮于侧栏顶条，悬停出深色符号，点击走 Ryn 对 `data-webview-close/minimize/maximize` 的委托监听（`window.*` IPC）。**不用 Ryn `Overlay` + `TrafficLightPosition` 原生红绿灯**：v0.6.1 冒烟实证 mac x64（Rosetta，macOS 26 runner）窗口创建后约 1 秒 segfault（release run 37532237576 两轮同签名，arm64 同批全绿；Frameless 路径由 0.6.0 mac x64 全链冒烟实证安全）——原生灯路径在 Ryn 上游修复前弃用，三端像素级一致反而更贴合「无差别」。
- **拖拽**：Ryn `TitleBarAutoDragHeight = CaptionBarOptions.HeightPx`（默认 52 = 官方顶条高）——webkit 不认 `-webkit-app-region`（Chromium 专属），Ryn 的 mousedown 裁决拖拽条是等价物：顶条内非交互点拖拽、双击缩放，交互元素自动排除。
- **注入面**：`StartupSequence.SetupCaptionBar` 双路（接线时一轮 + `RynNavigationCallbacks.SetOnNavigatedPersistent` 常驻导航钩子每次到达后重挂），脚本幂等、按当时 locale 现取，挂监督器取消令牌；失败仅留痕（托盘菜单是窗口控制退路）。能力面：ryn.json `window` 节细粒度 allow-list 恰好覆盖 Ryn titlebar 脚本调用的六命令（`RynWindowCapabilityTests` 钉死集相等）。
- **可调参数**：拖拽条高 `CaptionBar.HeightPx`（默认 52，非正值回退）。

FULL 评审三轮：#1（2026-10-07）抓出 innerHTML 裸拼单引号致 JS 解析即炸——innerHTML 全段走 `JsString` 管线 + 解析回归钉（`node` 实证 `new Function` 可解析）；#2（同日，v2 重做批）抓出 Win/Linux 路缺透明链扁平化（暗色主题侧栏洗白）——扁平化并入 `Build()`；横幅堆叠基准点回退窗口顶（顶条属 dsh 自有 UI，横幅为临时覆盖层）；#3（同日，v3 批）随 mac x64 segfault 定案复核实修面（darwin 标记 + 扁平化 + 查重守卫合一、死码 `BuildPlatformMark` 删除）。

## Alternatives considered

- **三端 Frameless + 自绘 40px caption 色带（第一版实现，实机对照推翻）**：官方 Windows 确有 caption 带，但 macOS 官方是无带满幅；自绘带配色无论用 dsh token 还是官方调色板都会在与页面拼接处出现色差线，且 macOS 上与「红绿灯嵌侧栏」的官方观感完全不符。需求方明确三端统一 macOS 形态后此路废弃。
- **三端统一官方 Windows 形态（带 + overlay 键，落败）**：机制可行（喂 `win32` 标记 + 注入带），但需求方指定 macOS 形态为目标。
- **`TitleBarStyle.Hidden`（落败）**：留空原生条，与满幅目标相反。
- **macOS 用 Overlay 原生红绿灯 + `TrafficLightPosition`（v2 采用后实证落败）**：官方同款机制，arm64 冒烟全绿，但 mac x64（Rosetta，macOS 26 runner）窗口创建后约 1 秒 segfault（两轮同签名，`Segmentation fault: 11`），0.6.1 tag 因此未发布。Ryn 0.38.0 上游的原生灯路径在 Rosetta 下不可用；上游修复后如需原生灯（悬停符号、全屏联动）可按平台重新启用，需先过 mac x64 冒烟。
- **`window: true` 整前缀放行（落败）**：dsh 页面是上游不可控内容，`setSize/setPosition/setFullscreen` 等命令面无必要暴露。

## Consequences

- 买到的：三端一致的官方 macOS 观感（满幅、红绿灯嵌侧栏顶条、无色带无色差线）；呈现规则全部来自 dsh web 内建（壳侧零样式维护，上游对 darwin 呈现的后续改进随页面升级自动获益）。
- 付出的：`data-platform='darwin'` 是「对页面说谎」——页面上一切依赖真实平台的分支（目前只有呈现）都按 darwin 走；上游若新增 darwin-only 行为（如 macOS 更新通道提示）会在三端同时生效。毛玻璃（vibrancy）以扁平底色近似，透明窗口 + BackdropMaterial 留作后续。红绿灯 maximize 是窗口缩放（toggleMaximize）而非 macOS 全屏。
- Ryn 自动拖拽条是「顶 52px 内非交互即拖拽」的近似，与官方逐 chrome 行声明的拖拽面不逐像素等价；dsh 顶区内交互元素（按钮/链接/输入）自动排除。
- wwwroot 占位页与注入脚本共享同一 id 与点簇规格（`CaptionBarTests` 与 `PageHealthMonitorTests.ShellDocuments_HaveVisibleText_StayAlive` 各钉一面）；占位页非 dsh web，无 darwin 呈现规则，保持自有居中布局。

## Testing

- `CaptionBarTests`：darwin 标记（先于幂等守卫）、红绿灯序与系统色、无色带（无 padding-top/无全宽背景）、innerHTML 转义回归、可达名双语。
- `CaptionBarOptionsTests`：默认 52、缺节/非正值/坏 JSON 回退矩阵。
- `RynWindowCapabilityTests`：ryn.json window allow-list 与 Ryn 注入脚本六命令集相等。
- `node` 实证：mark/build 脚本 `new Function` 解析通过 + DOM stub 下 `data-platform=darwin` 写入。
- 实机验收项（发布前）：三端顶区观感对照官方、红绿灯悬停符号、拖拽/双击缩放、最大化后行为、托盘退路。

## Deferred

- macOS vibrancy（透明窗口 + Ryn BackdropMaterial）——当前以 bg-base 扁平化近似。
- `html[data-fullscreen]` 全屏标记回流（官方 darwin 全屏红绿灯消失后的布局放松）。
- 红绿灯 maximize 的全屏语义对齐（当前为缩放）。

## Related

- [window-geometry-ryn-native-persist](2026-10-04-window-geometry-ryn-native-persist.md)——窗口几何持久化同为 Ryn 内建能力单点接线；无边框不触碰几何事件。
- [shell-tray-hide-to-tray](../architecture/2026-08-24-shell-tray-hide-to-tray.md)——关窗闸门挂窗口 `Closing`，是注入失败的窗口控制退路。
- [host-ui-locale](2026-08-28-host-ui-locale.md)——红绿灯可达名与横幅同走 UiCopy 双语单点。
- 上游参照：deepseek-harness `.agents/notes/archived/feature/2026-09-13-macos-hidden-titlebar-vibrancy.md`（darwin 呈现规则的家）。
