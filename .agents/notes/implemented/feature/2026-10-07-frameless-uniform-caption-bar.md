# Agent Note: 无边框窗口与壳自绘 macOS 观感

Status: implemented

Review: FULL/2026-10-07#5/R1=ok R2=ok R3=ok

三审结论：R1 注入脚本四件拆分与 `macChrome` 单布尔承载为最小形态、`greenAttr` 单源；R2 能力面「恰好集」七项同源、注入仍全走 `JsString`、直绑 `bar.children[2]` 与 Ryn capture 委托无冲突、几何判据失败默认方向安全、平台闸两处同谓词；R3 ADR 骨架/Related/上游参照路径经核实（`--dsw-specific-sidebar-fill`、上游 note 在 `implemented/`）、Deferred 只留未做项。三轮评审：首轮 0 Blocker（4+5+3 条建议）、验轮 2 与验轮 3 各收敛至「改名残留 + 措辞失实」类，末轮 2 条 Blocker（悬空 cref、ADR 测试段仍写已删的一次性标志）当场修复并 grep 自证（旧符号全仓零命中），未再开第 4 轮（轮次到顶）。macOS 观感与「不崩」仍需真机/发版冒烟腿验收。

## Problem

壳窗口默认是平台原生标题栏，与官方 DeepSeek 桌面端的无边框观感割裂：标题条占纵向空间、观感随平台各自为政。需求方要求三端统一官方 **macOS** 截图呈现的形态：内容满幅到顶、无独立标题带、红绿灯悬浮在侧栏顶区（且折叠按钮与红绿灯同行）。Ryn 0.38.0 内建 chrome 控制面（`TitleBarStyle`、自动拖拽条、`data-webview-*` 声明式窗口控制），具备零自研底座的条件。

## Decision

**三端统一 `TitleBarStyle.Frameless` + 壳自绘 macOS 观感**：窗口是我们的，量自己的 chrome、画自己的按钮——**不向 dsh 客户端冒充任何宿主身份**。

- **窗口**：`Frameless`（去原生 chrome，含 macOS 红绿灯）+ `TitleBarAutoDragHeight = CaptionBarOptions.HeightPx`（默认 52 = 官方 macOS 侧栏顶条；webview 不认 `-webkit-app-region`，Ryn 的 mousedown 拖拽条是等价物，双击缩放内建）。
- **注入脚本**（`PageBridge.CaptionBar.Build`，幂等可重挂；除 macOS 两项差异外三端同款）：
  1. 把宿主 chrome 高度登进 dsh 的公开变量 `--dsh-frame-top-clearance` / `--dsh-frame-chrome-top`（前者由客户端 JS 无条件读取以让浮层避开宿主 chrome、后者供浮层遮罩消费）——这是宿主本职，不是冒充。
  2. **侧栏列顶部让位** `padding-top`：padding 属元素自身背景盒，让位带被侧栏自身填充色无缝覆盖 → 无色带、无色差（`[class*="sidebarCol"]` 后缀选择器 + `:where(:has(> [data-shell-bottom]):has(> :nth-child(2)) > :first-child)` 结构式双兜底；侧栏列无稳定 data-* 钩子，真实 DOM 实测 frame > `.<hash>_sidebarCol`。兜底式以 `:where()` 归零权重并要求父级另有第二子——侧栏列挪位时最坏只是不再命中，不会把让位间距误打到内容列首元素上）。
  3. **折叠按钮回到让位带右端**（官方 darwin 布局把它放顶条里、与红绿灯同行）：`transform` 上移（不改布局盒，x 与侧栏宽度无关）+ 同批放开品牌行 `overflow` 裁剪（不放开则几何到位却画不出来）、不新增自绘按钮（会与原生重复）；折叠态把裁剪与位移一并还原，按钮留在轨内可点。
  4. **红绿灯三点**：12px 系统色圆点（`#ff5f57/#febc2e/#28c840`），位置对齐官方 `trafficLightPosition(16,18)`，悬停出深色符号；点击走 Ryn 对 `data-webview-close/minimize/maximize` 的委托监听（`window.*` IPC，能力面 = ryn.json `window` 节细粒度 allow-list，`RynWindowCapabilityTests` 钉集相等）。**绿灯在 macOS 另走原生全屏**（官方 macOS 绿灯是 Enter Full Screen，窗口缩放另有其键）：属性换成本壳自有 `data-dsh-fullscreen` + 自绑点击调 `window.setFullscreen`，可达名随之改（`UiCopy.CaptionFullscreenName`）；Ryn 0.38 无 fullscreen 查询命令，故不存标志位而用无状态几何判据（`|innerHeight-screen.height|≤2 且 |innerWidth-screen.width|≤2` ⇒ 判为已全屏；全屏铺满整屏且菜单栏自动隐藏，窗口缩放只占工作区、高度差 &gt; 2），每次点击现算，导航与外部退出都不会让状态漂移。
  5. **注入条保持可命中**（不设 `pointer-events:none`）：Ryn 自动拖拽条按 mousedown 的活体命中元素判定，**盖住条带却向下延伸的元素一律按内容处理、不拖拽**（Ryn `docs/custom-title-bars.md`）；本壳侧栏列的**元素盒**仍覆盖顶带（本壳只给它加 `padding-top`），盒高全窗 ⇒ 按内容处理，故这条自绘条（盒高 52 ≤ 阈值 78）是**侧栏让位区内**唯一可拖拽的命中体，透明化即失去该区拖拽（dsh 中间列头部自身 ≤78px，那一带另有 dsh 自己的可拖面；本壳可拖面 = 条盒 ≈68×52 扣掉三个 12×12 圆点的余量）。它盖住的正是自家 padding 造出的空白侧栏区（区无 dsh 控件），吞事件在此不损失可达性。
- **macOS 毛玻璃**（官方 vibrancy 观感，`OperatingSystem.IsMacOS()` 分支）：窗口侧 `RynOptions.Backdrop = Blur`（Ryn 会把窗/网页背景清成透明，再由 `NSVisualEffectView` 垫材质；其余平台 backdrop 后端降级 None，故显式择值、不静默降级——非 macOS 恒 `None`，否则只剩透明窗露底）；页面侧由注入脚本补官方那条**透明链**（`html,body` 透明 + 侧栏列改 80% 不透明的 `color-mix` 色调，材质经此透出）。官方侧栏色标取 `--dsw-specific-sidebar-fill` 的渐变叠色，且整链以 `html[data-platform='darwin']` 门控，而该属性正是本壳禁用的宿主冒充开关（见 Alternatives）；故改为注入期按平台取舍、色标改用通用 `--dsw-alias-bg-base` 的 80% 不透明 `color-mix` 近似（**已知偏离**：与 dsh 自带 darwin 规则的取色/减透明回退不同，观感以真机为准）。
- **能力面**：`window.setFullscreen` 进 ryn.json allow-list，`RynWindowCapabilityTests` 的「恰好集」随之扩到七项（Ryn 注入脚本六命令 + 本壳绿灯命令）。Ryn 的能力面文件无平台条件字段，故该命令在 Windows/Linux 同样被放行（**已知扩大**：那两端无消费点，仅 macOS 分支调用；宁可显式登记也不引入平台分裂的能力文件）。
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
- 与官方 macOS 观感的已知差异：折叠时官方完全隐藏侧栏（把重开控件挂进 frame 的 leading 座位），本壳折叠仍走 dsh 的窄轨布局（轨道内容随让位整体下移到红绿灯之下）；macOS 全屏隐灯（官方 `html[data-fullscreen]` 让位/隐灯）未做——绿灯已切原生全屏，但全屏态下本壳自绘灯仍随注入条留在于顶区。
- macOS 面（原生全屏切换、毛玻璃）**尚未经过任何实机或 CI 运行验证**：能启动应用的 mac 腿只在 `package.yml` 里跑，而它的触发面是发布（`release.yml` 经 `workflow_call`，即 tag）与手动 `workflow_dispatch`；PR 期的 `ci.yml` mac 腿只跑两个过滤测试类、不启应用——本批只做了本机（Linux）单测与静态门禁，观感与「不崩」都需发版冒烟腿 + macOS 真机验收（见 Testing）。

## Testing

- `CaptionBarTests`：禁 `data-platform` / 禁 `data-windows-titlebar` / 禁 `dshDesktop` 三条红线回归钉；chrome 高度变量与侧栏让位同高；结构式兜底收窄（`:where(` + `:nth-child(2)`）；注入条**不得** `pointer-events:none`（拖拽面回归钉，评审 S4/R2，三端与 mac 脚本各钉一次）；折叠按钮归位与折叠态中和；红绿灯系统色与官方位置；幂等与样式查重守卫；innerHTML 经 JsString 转义（评审 B1）；可达名双语；macOS 分支（绿灯换 `data-dsh-fullscreen` + 直绑第三子 `bar.children[2].addEventListener('click'` 调 `window.setFullscreen`、不再出现 `data-webview-maximize`、透明链在）与非 macOS 分支「不越界」的对照。
- `CaptionBarOptionsTests`：默认 52、缺节/非正值/坏 JSON 回退矩阵。
- `RynWindowCapabilityTests`：ryn.json window allow-list 与「Ryn 注入脚本六命令 + 本壳 `setFullscreen`」集相等。
- 真实 DOM 实测（浏览器加载 dsh web，注入同一份样式，量几何 + 截图）：展开 红绿灯 (16,18..30)、折叠按钮 (240,12)、侧栏填充无缝；折叠 按钮 (10,70) 不与红绿灯重叠。
- 实机验收项（发布前）：三端顶区观感对照官方、拖拽/双击缩放、三键点击、折叠/展开、托盘退路、自更新链路；注入条覆盖顶区 0..52 的左上一段且**保持可命中**——它既是三键的落点也是该条带唯一的拖拽面，专核该区拖动/双击缩放正常、且其下（自家 padding 造出的空白侧栏区）无 dsh 控件被挡。**macOS 专项（本机无真机，须真机过）**：绿灯进出原生全屏（含用 ⌃⌘F 或菜单退出后再点绿灯能否正确反向、系统「自动隐藏菜单栏」+ Dock 自动隐藏时判据的盲区）、全屏态下注入条/红绿灯的观感与让位是否需收缩、毛玻璃透明链在当前主题（浅/深）下的侧栏色调与对比度（与 dsh 自带 darwin 取色不同）、窗口拖动/切换主题后材质是否稳定。

## Deferred

- `html[data-fullscreen]` 全屏态下的让位/红绿灯隐藏（官方全屏隐灯）——绿灯已切原生全屏，本项待 macOS 真机对照后再定形态。
- 折叠态与官方「隐藏侧栏 + 顶条重开控件」的对齐。

## Related

- [window-geometry-ryn-native-persist](2026-10-04-window-geometry-ryn-native-persist.md)——窗口几何持久化同为 Ryn 内建能力单点接线。
- [shell-tray-hide-to-tray](../architecture/2026-08-24-shell-tray-hide-to-tray.md)——关窗闸门挂窗口 `Closing`，是注入失败的窗口控制退路。
- [host-ui-locale](2026-08-28-host-ui-locale.md)——红绿灯可达名与横幅同走 UiCopy 双语单点。
- 上游参照：deepseek-harness `.agents/notes/implemented/feature/2026-09-13-macos-hidden-titlebar-vibrancy.md`（darwin 呈现与透明链的实现细节；其 `data-platform` 前提是本篇第一号备选被否的原因）。
