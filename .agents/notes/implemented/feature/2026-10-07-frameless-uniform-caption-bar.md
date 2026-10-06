# Agent Note: 三端统一无边框窗口与自绘标题栏

Status: implemented

Review: FULL/2026-10-07/R1=ok R2=ok R3=ok

## Problem

壳窗口默认是平台原生标题栏（`TitleBarStyle.Native`），与官方 DeepSeek 桌面端的无边框观感割裂：标题条占用纵向空间、观感随平台各自为政，且与 dsh web 内容的视觉风格不连续。用户要求三端统一无边框、保留最小化/最大化/关闭三个窗口控制并保持官方一致的外观。Ryn 0.38.0 已内建完整的 chrome 控制面（`TitleBarStyle` 枚举、自动拖拽条、`data-webview-*` 声明式窗口控制），具备零自研底座的对齐条件。

## Decision

FULL 评审（2026-10-07）抓出并修复：注入脚本 innerHTML 裸拼单引号致 JS 解析即炸（innerHTML 全段走 JsString 管线 + `Build_InnerHtmlViaJsString_NoRawSingleQuoteInjection` 解析回归钉）；allow-list 恰好集断言、钩子内按当时 locale 取脚本、注入挂监督器取消令牌三项 suggestion 同批落地。

三端统一取 `RynOptions.TitleBarStyle = Frameless`（原生 chrome 全去，含 macOS 红绿灯），窗口控制与拖拽由注入页面的自绘顶栏（caption bar）承担：

- **窗口配置**（组合根 `BuildApp` 的 `ConfigureOptions`）：`TitleBarStyle.Frameless` + `TitleBarAutoDragHeight = CaptionBarOptions.HeightPx`（自动拖拽条与顶栏高度同源，双击缩放由 Ryn 拖拽条内建）。配色不进配置：主路消费 dsh 主题 token（`--dsw-alias-bg-overlay/label-primary`，随页面明暗），回退值 = 官方 caption 规格（亮 `#f9fafb/#0f1115`、暗 `#1b1b1c/#f9fafb`，经 prefers-color-scheme 的自定义属性间接层）。
- **注入脚本**：`PageBridge.CaptionBar.Build`（纯函数单点）生成幂等脚本——固定 id `dsh-desktop-caption-bar` 的 fixed 顶栏，整条 `data-webview-drag`，三个按钮只带 `data-webview-minimize/maximize/close` 声明式属性，**零事件处理器**（点击语义全由 Ryn 注入脚本的委托监听走 `window.*` IPC）；可达名（aria-label）经 `UiCopy.CaptionButtonNames` 随宿主 locale 取值。内容让位利用 dsh 布局链 `html/body/#root{height:100%}` 的百分比特性：`body{padding-top:H!important;box-sizing:border-box!important}` 整链干净下移，无底部剪裁。
- **注入时机**：`StartupSequence.SetupCaptionBar` 接线两路——接线时立即一轮有界重试注入（`PagePump.InjectCaptionBarWhenReadyAsync`，与横幅共用重试单点 `RetryInjectWhenReadyAsync`，挂监督器取消令牌），另经 `RynNavigationCallbacks.SetOnNavigatedPersistent`（新增的常驻导航钩子，不占用单槽 `SetOnNavigated`）在每次页面到达后重挂——顶栏 DOM 随整页导航销毁，初次加载/自愈重载/引导后接管都必须重注入；脚本在钩子内按当时 locale 现取（语言切换后导航即换语言）。
- **能力面**：ryn.json 的 `capabilities` 新增 `window` 节，细粒度 allow-list 只放行 Ryn titlebar 脚本实际调用的六命令（`close/minimize/toggleMaximize/startDrag/startResize/beginNativeDrag`），不放行 `setSize/setPosition/setFullscreen` 等其余 window 命令；全集由 `RynWindowCapabilityTests` 钉死。
- **占位页**：wwwroot/index.html 静态内置同 id 同规格顶栏（引导页先于注入存在，注入脚本遇同 id 即让位）；横幅堆叠（`DesktopBanner.Build`）的基准点改为顶栏实际高度（`base + n*44`），顶栏未注入时行为与旧基准点一致。
- **可调参数**：高度 `CaptionBar.HeightPx`（appsettings 节 `CaptionBar`，`CaptionBarOptions` record，非正值回退默认 40 = 官方 `WINDOWS_TITLEBAR_HEIGHT`）。
- **退路**：顶栏注入失败仅留痕不重试到死——hide-to-tray 关窗闸门挂在窗口 `Closing` 上（不依赖顶栏），托盘菜单的唤回/最大化/退出始终可用。

## Alternatives considered

- **`TitleBarStyle.Overlay`（落败）**：macOS 上保留原生红绿灯浮层。落败原因：与自绘三按钮叠出两套控制，破坏「三端无差别」；且红绿灯位置与自绘栏配色协调要额外维护 `TrafficLightPosition`。
- **`TitleBarStyle.Hidden`（落败）**：留一条空的原生标题条。落败原因：内容渲染在条下方，纵向仍有原生条占位，与无边框目标相反。
- **仅 Win/mac 无边框、Linux 保留原生（落败，官方即此形态）**：官方上游正是按平台分治（Win `titleBarOverlay`、mac `hiddenInset`、Linux 原生）。落败原因：需求方明确要求三端无差别；Ryn 的声明式控制在 WebKitGTK 上同样工作，无平台豁免的技术必要。
- **`window: true` 整前缀放行（落败）**：少写几行 allow-list。落败原因：代理源上的 dsh 页面是上游不可控内容，`setSize/setPosition/setFullscreen/setAlwaysOnTop` 等命令面暴露面无必要扩大；细粒度 allow-list 是 ryn.json 沙箱模型的用法本意。
- **一次性注入（落败）**：只在启动时注入一次。落败原因：顶栏 DOM 随整页导航销毁——页面健康监视的有界 reload、鉴权自愈重载、引导完成后的代理页接管都会换文档，一次性注入在这些路径上全部失效；常驻导航钩子是唯一全覆盖点。
- **复用单槽 `SetOnNavigated`（落败）**：不加新钩子，借现成信号。落败原因：该单槽被导航提交等待（`NavigateAndAwaitCommitAsync`）按需占用/清空，两消费者互相覆盖；单槽语义是「一次性到达信号」，常驻重注入与其语义不符。

## Consequences

- 买到的：三端一致的无边框观感与官方对齐的窗口控制；dsh 主题 token 让顶栏随页面明暗自适应；固定顶栏带来稳定的窗口控制/拖拽落点。
- 付出的：顶栏 z-index 恒最大——dsh 页面顶端锚定的 fixed 弹层（modal 遮罩等）会被顶栏压住一条 40px 带（modal 居中在视口而非让位后内容区，视觉上略偏）；最大化态按钮符号仍为方框（不区分还原符号，见 Deferred）；能力面新增 `window` 前缀六命令，Ryn 上游若给 titlebar 脚本加新调用需同步扩 allow-list（`RynWindowCapabilityTests` 只钉「至少齐」不钉「恰好」）。
- wwwroot 占位页与注入脚本共享同一 id 与几何（40px 默认），两处规格变更必须同批（`CaptionBarTests` 与 `PageHealthMonitorTests.ShellDocuments_HaveVisibleText_StayAlive` 各钉一面）；静态页拿不到运行时配置，`CaptionBar.HeightPx` 配非默认值时只影响注入页与拖拽区，占位页恒为 40——该旋钮实际是「注入页可调、静态页冻结默认」。

## Testing

- `CaptionBarTests`：幂等守卫、声明式属性、高度贯通（bar/让位两处）、主题 token 与官方回退值、可达名双语经 JsString。
- `CaptionBarOptionsTests`：默认值锁 40、缺节/非正值/坏 JSON 回退矩阵。
- `RynWindowCapabilityTests`：ryn.json window allow-list 覆盖六命令全集。
- `DesktopBannerTests`：堆叠基准点断言更新（顶栏高度计入）。
- 实机验收项（发布前）：dsh 明暗两主题下顶栏配色/让位布局、拖拽与双击缩放、最大化后符号、托盘退路与关窗闸门行为不变。

## Deferred

- 最大化态还原符号（`window.isMaximized` 驱动 glyph 切换）：需要窗口态事件回流，首版不做。
- 顶栏命中 dsh 页面顶部弹层的让位细节（modal 遮罩与顶栏的视觉关系）待实机观察后再调。

## Related

- [window-geometry-ryn-native-persist](2026-10-04-window-geometry-ryn-native-persist.md)——窗口几何持久化同为 Ryn 内建能力单点接线；本决定的 `PersistWindowState` 不受无边框影响（几何事件照常）。
- [shell-tray-hide-to-tray](../architecture/2026-08-24-shell-tray-hide-to-tray.md)——关窗闸门挂窗口 `Closing`，是顶栏注入失败的窗口控制退路。
- [host-ui-locale](2026-08-28-host-ui-locale.md)——顶栏可达名与横幅同走 UiCopy 双语单点。
