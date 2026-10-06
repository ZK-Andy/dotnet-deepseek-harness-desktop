# Agent Note: 无边框窗口与壳自绘 macOS 观感

Status: implemented

## Problem

壳窗口默认是平台原生标题栏，与官方 DeepSeek 桌面端的无边框观感割裂：标题条占纵向空间、观感随平台各自为政。需求方要求三端统一官方 **macOS** 截图呈现的形态：内容满幅到顶、无独立标题带、红绿灯悬浮在侧栏顶区（且折叠按钮与红绿灯同行）。Ryn 0.38.0 内建 chrome 控制面（`TitleBarStyle`、自动拖拽条、`data-webview-*` 声明式窗口控制），具备零自研底座的条件。

## Decision

**三端统一 `TitleBarStyle.Frameless` + 壳自绘 macOS 观感**：窗口是我们的，量自己的 chrome、画自己的按钮——**不向 dsh 客户端冒充任何宿主身份**。

- **窗口**：`Frameless`（去原生 chrome，含 macOS 红绿灯）+ `TitleBarAutoDragHeight = CaptionBarOptions.HeightPx`（默认 52 = 官方 macOS 侧栏顶条；webview 不认 `-webkit-app-region`，Ryn 的 mousedown 拖拽条是等价物，双击缩放内建）。
- **注入脚本**（`PageBridge.CaptionBar.Build`，三端同款，幂等可重挂）：
  1. 把宿主 chrome 高度登进 dsh 的公开变量 `--dsh-frame-top-clearance` / `--dsh-frame-chrome-top`（前者由客户端 JS 无条件读取以让浮层避开宿主 chrome、后者供浮层遮罩消费）——这是宿主本职，不是冒充。
  2. **侧栏列顶部让位** `padding-top`：padding 属元素自身背景盒，让位带被侧栏自身填充色无缝覆盖 → 无色带、无色差（`[class*="sidebarCol"]` 后缀选择器 + `:has(> [data-shell-bottom]) > :first-child` 结构式双兜底；侧栏列无稳定 data-* 钩子，真实 DOM 实测 frame > `.<hash>_sidebarCol`）。
  3. **折叠按钮回到让位带右端**（官方 darwin 布局把它放顶条里、与红绿灯同行）：`transform` 上移（不改布局盒，x 与侧栏宽度无关）+ 同批放开品牌行 `overflow` 裁剪（不放开则几何到位却画不出来）、不新增自绘按钮（会与原生重复）；折叠态把裁剪与位移一并还原，按钮留在轨内可点。
  4. **红绿灯三点**：12px 系统色圆点（`#ff5f57/#febc2e/#28c840`），位置对齐官方 `trafficLightPosition(16,18)`，悬停出深色符号；点击走 Ryn 对 `data-webview-close/minimize/maximize` 的委托监听（`window.*` IPC，能力面 = ryn.json `window` 节细粒度 allow-list 六命令，`RynWindowCapabilityTests` 钉集相等）。
- **注入时机**：`StartupSequence.SetupCaptionBar` 双路（接线时一轮 + `RynNavigationCallbacks.SetOnNavigatedPersistent` 常驻导航钩子每次导航到达后重挂），脚本按当时 locale 现取，挂监督器取消令牌；失败仅留痕（托盘菜单是窗口控制退路）。
- **占位页** `wwwroot/index.html` 内置同 id 同规格点簇（壳自有引导页无 dsh 侧栏，自带 52px 让位）。

### 官方 macOS 折叠侧栏后红绿灯的行为（源码对照，本节为参照系）

上游 `apps/desktop` 与 `packages/client/ui-layout/src/client/AppFrame.module.css`：折叠时**红绿灯完全不动**（AppKit 原生按钮，恒在 (16,18)、占位到 x=68）；侧栏列**整个隐藏**（darwin 下 `collapsedWidth = 0`，**无窄轨**——56px 窄轨是非 darwin 平台的行为）；折叠期挂载 `shell.leading` 座位（`top: 11px; left: 88px`，两个 28px 控件：展开侧栏 + 新会话，中线 y≈24 与红绿灯齐平），并发布 `--dsh-frame-leading-clearance: 160px`（红绿灯到 68 + 控件 88..152 + 8px 空隙）供顶到左上角的主面板让位；全屏时 macOS 隐灯，座位左移 `left:12px`、让位降到 84px。

本壳的偏离点：我们没认领 darwin（认领即冒充宿主、插件崩），折叠后拿到的是 dsh 的非 darwin 窄轨，三点（16..68）会跨到内容区上——故折叠态把三点收进轨内（内缩 10 + 3×10 + 2×6 = 52 ≤ 轨宽），不做「整列隐藏 + 座位重开控件」的完整复刻（那需要 darwin 布局）。

## Alternatives considered

- **注入 `data-platform='darwin'` 借用 dsh 内建 macOS 呈现（v0.6.1/0.6.2 实现，实测崩溃后废弃）**：该属性不是样式开关，而是 dsh 客户端的**桌面运行时开关**——`detectEnvironment` 见之即判 `runtime="desktop"`（官方 Electron 宿主语义），`dsh-client-shortcuts` 随即要求 `window.dshDesktop.keyboard`（官方 preload 桥，非 Electron 宿主不存在）并 `throw`，级联 25 个 UI 插件 pending，启动即显示「插件加载失败」屏。**实证**：v0.6.1 release 与 v0.6.2 首发安装后各一次；代码级根因见 `dsh-client-shortcuts/lib/client.js` 的 `detectEnvironment` 与 `ShortcutsService` 构造。冒充宿主身份的方案从此禁止。
- **注入 `data-windows-titlebar`（落败）**：纯 CSS 开关、不触发运行时误判，但那是 dsh 的 Windows 呈现（顶带 + 内容圆角卡片），非需求方指定的 macOS 形态。
- **注入 `window.dshDesktop` 假桥后继续用 `data-platform`（落败）**：桥面实测很小（`keyboard.closeWindow/subscribe` + `shortcuts` + `deviceInfo`），能骗过插件构造；但 `runtime="desktop"` 会把这些插件的**可配置快捷键判给「原生菜单」**（`installKeyboard(..., native=true)` 时 DOM 只喂固定动作），而本壳没有原生菜单——快捷键会静默失效。骗得过去 ≠ 不付代价。
- **`TitleBarStyle.Overlay` + `TrafficLightPosition` 原生红绿灯（v0.6.1 实现，实测崩溃后废弃）**：mac x64（Rosetta，macOS 26 runner）窗口创建后约 1 秒 `Segmentation fault: 11`（同批 arm64 全绿）；Ryn 0.38.0 该路径在 Rosetta 下不可用。上游修复后如需原生灯（悬停符号、全屏联动）可按平台回归，须先过 mac x64 冒烟腿。
- **`TitleBarStyle.Hidden`（落败）**：留空原生条，与满幅目标相反。
- **整条自绘 caption 色带（v0.6.0 实现，实机对照后废弃）**：自绘带用独立底色，与 dsh 页面拼接处必然出现色差线（需求方实机否决），且 macOS 上形态不符——正解是让让位带由侧栏自身填充覆盖（本轮）。
- **`window: true` 整前缀放行能力（落败）**：dsh 页面是上游不可控内容，`setSize/setPosition/setFullscreen` 等命令面无必要暴露。

## Consequences

- 买到的：三端一致的官方 macOS 观感（满幅、无缝让位带、红绿灯与折叠按钮同行）；不冒充宿主身份，dsh 客户端插件行为与浏览器渲染一致（全部正常激活，快捷键仍由网页侧派发）。
- 付出的：让位与折叠按钮定位依赖 dsh 侧栏的类名后缀（`sidebarCol`/`toggle`/`collapsed`）与内部行内边距（上移量的 `+10px` 余量），dsh 大改侧栏结构时需随之校准——失效形态是「按钮/内容位置偏移」而非崩溃；`--dsh-frame-top-clearance`/`--dsh-frame-chrome-top` 是 dsh 客户端**读**的公开量，上游若改名同样只需校准。
- 与官方 macOS 观感的已知差异：折叠时官方完全隐藏侧栏（把重开控件挂进 frame 的 leading 座位），本壳折叠仍走 dsh 的窄轨布局（轨道内容随让位整体下移到红绿灯之下）；红绿灯绿色键是窗口缩放（`toggleMaximize`）而非 macOS 原生全屏；毛玻璃（vibrancy）未做，侧栏为纯色填充。

## Testing

- `CaptionBarTests`：禁 `data-platform` / 禁 `data-windows-titlebar` / 禁 `dshDesktop` 三条红线回归钉；chrome 高度变量与侧栏让位同高；折叠按钮归位与折叠态中和；红绿灯系统色与官方位置；幂等与样式查重守卫；innerHTML 经 JsString 转义（评审 B1）；可达名双语。
- `CaptionBarOptionsTests`：默认 52、缺节/非正值/坏 JSON 回退矩阵。
- `RynWindowCapabilityTests`：ryn.json window allow-list 与 Ryn 注入脚本六命令集相等。
- 真实 DOM 实测（浏览器加载 dsh web，注入同一份样式，量几何 + 截图）：展开 红绿灯 (16,18..30)、折叠按钮 (240,12)、侧栏填充无缝；折叠 按钮 (10,70) 不与红绿灯重叠。
- 实机验收项（发布前）：三端顶区观感对照官方、拖拽/双击缩放、三键点击、折叠/展开、托盘退路、自更新链路；**注入条独占左上约 68×52px 且吞指针事件**，需专核该区域下 dsh 侧栏控件的点击与拖拽角落（评审 S4：最坏为交互退化，不崩）。

## Deferred

- macOS vibrancy（透明窗口 + Ryn BackdropMaterial）。
- `html[data-fullscreen]` 全屏态下的让位/红绿灯隐藏（官方全屏隐灯）。
- 折叠态与官方「隐藏侧栏 + 顶条重开控件」的对齐。

## Related

- [window-geometry-ryn-native-persist](2026-10-04-window-geometry-ryn-native-persist.md)——窗口几何持久化同为 Ryn 内建能力单点接线。
- [shell-tray-hide-to-tray](../architecture/2026-08-24-shell-tray-hide-to-tray.md)——关窗闸门挂窗口 `Closing`，是注入失败的窗口控制退路。
- [host-ui-locale](2026-08-28-host-ui-locale.md)——红绿灯可达名与横幅同走 UiCopy 双语单点。
- 上游参照：deepseek-harness `.agents/notes/archived/feature/2026-09-13-macos-hidden-titlebar-vibrancy.md`（darwin 呈现的实现细节；其 `data-platform` 前提是本篇第一号备选被否的原因）。
