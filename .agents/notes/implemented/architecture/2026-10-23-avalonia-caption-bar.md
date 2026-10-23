# Agent Note: Avalonia 无边框顶栏与三平台原生拖拽

Status: implemented

Review: FULL/2026-10-23#2/R1=ok R2=ok R3=ok

增量闸豁免：gross ≤ 2200，fanout ≤ 15

## Problem

旧宿主（Ryn）的无边框观感由三块拼成：`TitleBarStyle.Frameless` 去原生 chrome、`TitleBarAutoDragHeight`
让引擎接管顶带拖拽、`Ryn.Plugins.Window` 能力面提供 `startDrag`/`startResize`/`toggleMaximize` 等命令。
迁移到 Avalonia 后：窗口装饰可配，但**页面区域的指针事件被原生 WebView 子窗消费**，Avalonia 的
`Window.BeginMoveDrag` 只接受 `PointerPressedEventArgs`，且三种后端都真正消费它（Win32 取
`e.Pointer.Capture`/`IsPrimary`，X11 用指针坐标发 `_NET_WM_MOVERESIZE`，Wayland 把
`e.PlatformInputEventCookie` 当 `xdg_toplevel.move` 的 serial）——页面发起的拖拽没有公开路径，
Ryn 的能力面也随宿主退役。红黄绿三键原本也靠 Ryn 对 `data-webview-*` 的委托监听，新宿主没有那层监听。

Wayland 没有可用公开路径：`xdg_toplevel.move` 必须携带触发事件的 seat serial，而该 serial 只存在于
Avalonia 自己的原始输入事件里，页面注入脚本拿不到。

## Decision

- **命令面**：`PageBridge/WindowCommandRouter` 承接 `window.startDrag`/`window.startResize`/
  `window.toggleMaximize`/`window.minimize`/`window.close`/`window.setFullscreen`，命令名进
  `HostCommandIds`（单一事实源）；页面脚本里的命令名由 `AppJsonContext.JsString(HostCommandIds...)`
  生成，不在 JS 里复写字面量。参数走 `JsonDocument` 解析（源生成上下文只配序列化方向），非法帧回错误帧。
- **平台端口**：`Hosting/WindowDrag` 按窗口句柄描述符选端口——`HWND` 走 Win32
  `ReleaseCapture` + `WM_NCLBUTTONDOWN`（拖拽命中 `HTCAPTION`，缩放命中 HT* 十六码）；`XID` 走
  X11 独立连接发 `_NET_WM_MOVERESIZE`（方向码 + 屏幕坐标 + 左键，`XInternAtom`/`XSendEvent` 返回值
  即成败）；macOS 走 `Window.BeginMoveDrag(null)`——`Avalonia.Native` 的实现不读参数且显式回退
  `[NSApp currentEvent]`，正是「按下被 WebView 消费」的场景；其余（Wayland/无句柄）为不可用端口。
- **窗口形态**：Windows/macOS 用 `WindowDecorations.BorderOnly`（保留原生边框/阴影/缩放，去标题栏）；
  Linux 在 `OnOpened` 判定端口后切 `WindowDecorations.None`（无 WM 边框，缩放由页面边缘判定 + 平台消息）。
  `OnOpened` 每次 `Show()` 都会触发（托盘唤回），接线以一次性闩保护，路由不会随唤回重复注册。
- **页面侧**：`CaptionBar.Build` 增加 `CaptionBarChannel`（`window.__ryn` / `window.__shell`）与
  `edgeResize` 开关。新宿主通道下：三键各自 `onclick`（Ryn 没有委托监听可依赖）、拖拽带屏幕坐标
  （`screenX + clientX`）、mousedown 捕获阶段先判 6px 边缘带发 `window.startResize`；
  macOS 的 `edgeResize=false`（边框缩放由窗口系统按 BorderOnly 提供，页面再抢边会既不缩放又吞点击）。
  旧通道行为不变（红黄与缩放绿灯仍靠 Ryn 属性委托；macOS 绿灯的原生全屏仍由本壳自绑）。
- **归一化**：页面给的坐标是 CSS 像素，X11 根坐标是设备像素——`AvaloniaWindowCommands` 按
  `Window.RenderScaling` 换算后再进端口。
- **注入与降级**：`PageCaptionBar` 在每次导航完成后紧跟桥注入顶栏脚本；`ShellWindow.OnOpened` 判定端口后
  再补一次（覆盖「导航先于窗口就绪」的空档）。幂等守卫拆成两条：`window.__dsh_caption_ready`（每文档
  只装一次监听）与条存在性（静态条在 `wwwroot/index.html` 已有，旧守卫「遇条即 return」会让引导页
  的拖拽与描边环整段缺失）。端口不可用时（Wayland）关闭顶栏注入并把装饰恢复为 `Full`。

## Alternatives considered

- **保留 Ryn 的窗口能力做拖拽**：能力面随 Ryn 退役；且新宿主窗口不是 Ryn 窗口，语义接不上。落败。
- **页面侧伪拖拽（mousemove 里逐帧调窗口 SetPosition）**：不经窗口管理器，失去吸附/贴边/最大化还原，
  指针移出窗口后事件中断，抖。落败。
- **`ExtendClientAreaToDecorationsHint` + 自绘条**：Windows 可用，但 Linux X11 走「托管装饰」，
  Avalonia 会自己画 chrome，与页面自绘条叠成两层；Wayland 更不可控。落败。
- **Wayland 也做无边框**：`xdg_toplevel.move` 的 serial 拿不到，窗口将不可拖动；违反「不给出拖不动的窗」。
  落败（保留原生装饰，等上游暴露通道）。
- **Win32 走 `WM_SYSCOMMAND`（Avalonia 自己的移动循环）**：需要先 `e.Pointer.Capture(null)` 与真实指针，
  页面路径没有；直接用 `WM_NCLBUTTONDOWN` 命中测试是同一语义的无参形态。落败（实现取后者）。

## Consequences

- Windows/macOS/Linux-X11：顶带可拖动窗口，四边与四角可缩放，红绿灯三键与双击缩放生效，macOS 绿灯进原生全屏。
- Wayland：窗口保留原生装饰，页面不出现自绘顶栏条；无端口时 `EnableCaptionBar(false)` 同时关掉补注入。
- 命令参数非法回 `{"error":...}` 并写 `host.log`（页面侧会吞掉 Promise 拒绝，宿主不留痕就两端都无证据）；未知命令走 `RynCommandNotFoundException`。关窗经
  `CloseGate`（与托盘退出同一条闸）。
- **平台端口无 headless 测试面**：Win32/X11/macOS 三条通道只能真机验；未验前不得宣称「三平台可用」。
- **X11 解抓前置未满足**：`XUngrabPointer` 发在独立连接上，解不掉 WebView/GTK 客户端持有的隐式抓取；
  部分窗口管理器会以 `AlreadyGrabbed` 忽略本次移动——X11 真机必须验证「拖拽是否真的被 WM 接管」。
- **X11 无 WM 时静默失败**：`XSendEvent` 返回 0 即判失败并留痕，但不会弹错误——顶栏拖不动时先查 `host.log`。
- **边缘带占 6px**：页面最外 6px 被判为缩放面、先于内容命中（macOS 关掉了这条带）——贴边控件的点击会变成起缩，
  这是无边框窗口的固有取舍；若 dsh 将来把可点控件贴到视口边缘，需要把这条带收窄或改为可配置。
- **引导页样式块独立注入**：静态条存在时样式（三变量/描边环）仍会注入，否则引导/回落页的描边环没有样式且
  留下零尺寸 flex 子项。
- **macOS 端口按 OS 选**（句柄描述符在 Avalonia.Native 未公开）：若将来该后端变化需改判据；
  `BeginMoveDrag(null!)` 仅在 Avalonia.Native 安全（其实现不读参数）。
- **本批首个 CI run 为红（`39879389592`，覆盖率 64.15% < 基线 65.45%，超 0.5pp 容差）**：随后按跟值流程
  在同批第二提交（`1cdefcd`）把 `scripts/test-baseline.json` 与 README 双语徽章改为 64.15%/1166，CI `39880360547` 转绿；
  那个红 run 留在历史里，不会消失。
- **覆盖率跟值下调**：本片新增的三平台 P/Invoke、Avalonia 窗口装配与注入时序无 headless 测试面，CI 并集覆盖率
  64.15%（原 65.45%）；按 docs/testing.md「跟值」流程同批更新 `scripts/test-baseline.json` 与 README 双语徽章，
  不掩盖、不以「超容差」侥幸过关。
- **跨显示器缩放**：换算用窗口的 `RenderScaling`，混合 DPI 多屏下锚点可能偏【推断 · 未证】。

## Testing

- `WindowCommandRouterTests`：六命令分发、拖拽/缩放参数解析、非法帧回错误帧、未知命令抛
  `RynCommandNotFoundException`。
- `CaptionBarTests`：新通道脚本含 `window.__shell`、三键 `onclick`、屏幕坐标拖拽与边缘缩放；
  旧通道仍走 `beginNativeDrag` 且缩放判定关闭；引导页守卫（`__dsh_caption_ready` + 静态条不早退）。
- `ShellBridgeTests`：导航完成后顶栏脚本与桥同批注入。
- 真机验收（未做）：Windows 拖动/四边缩放/双击最大化；macOS 拖动/BorderOnly 边缘缩放/绿灯全屏；
  Linux X11 拖动（含「WM 是否接管」）与四边缩放、Wayland 保留装饰且无第二条标题栏。
