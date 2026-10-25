# Agent Note: 退出硬退终态 + X11 首映射焦点兜底 + 缩放键去抖与最大化态边缘短路

Status: implemented

Review: FULL/2026-10-25#2/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

四组真机实证缺陷（0.7.2–0.7.4 真机 + 日志取证，单一事实源 `.plan/修复批-2026-10-25-退出秒杀-焦点兜底-按钮去抖.md`）：

1. **退出秒杀**：托盘重启/退出实测拖 10s+（旧实例打完最后一行日志后静默 13s+ 才死）。优雅退出链 `Close → GTK 窗口销毁 → WebKitGTK 原生拆除 → Shutdown` 在本机 WebKitGTK 上从未按时走完，每次退出实际由 8s 看门狗强杀——看门狗是事实退出路径而非保险。
2. **装后自动重启失焦**：scope+nohup 复现器取证（0.7.4 现场）：窗口 `WM_PROTOCOLS` 缺 `WM_TAKE_FOCUS`、`_NET_ACTIVE_WINDOW` 悬在无 WM_CLASS 幽灵窗上，下游媒体键/页内 tab/Alt-Tab 全废（键盘事件只投焦点窗）；统一假说已定案为焦点协议缺失，与 WebView 无关。
3. **最大化按钮双击净零**：按钮双击 = 两条 `toggle-maximize` 相隔 ~200ms，翻去翻回（0.7.4 真机日志 16:17:03-08 实锤）。
4. **最大化态边缘带误行为**：6px 边缘带仍吞贴边点击、发无效 resize、显缩放光标——边缘脚本不知道窗口已最大化。

另：顶栏余项⑤（日志标签与线名漂移）随批顺手收。

## Decision

**退出终态 = 硬退（用户拍板 A 方案「该杀全杀完立即硬退」）**：`ExitPipeline.OrderlyQuit/Restart` 在 ReapRuntime（杀 dsh 整树/释放 marker）→ releaseLocalPorts →（Restart 的 spawnSuccessor）完成后直接 `Environment.Exit(0)`，不投递关窗、不等 GTK/WebKit 拆除。硬退动作与看门狗一样做成可注入委托（测试记序 fake）；8s 看门狗保留为保险（硬退正常即时完成，永远轮不到）。管道 `closeWindow` 参数随之删除——关窗投递只归自更新面与恢复页（`RunWiring.CloseWindow` 共用面不变）。已接受的代价：跳过 `Closing` 几何落盘（退出前最后一次纯移动窗口的位置可能丢；最大化态即时存不受影响）与退场内容摘除，可能伴随 WebKitGTK 退场噪声（崩的只是壳尸首：dsh 已杀、端口已放、marker 已释）。

**X11 首映射焦点兜底 = 激活请求 + 有界复验**（`X11FocusBootstrap`，`ShellWindow.OnOpened` 首映射后调用，进程级 once）：首映射后向根窗口发 `_NET_ACTIVE_WINDOW` 激活请求（source indication=1，应用发起；与 `X11WindowDrag` 同型的根窗口 ClientMessage），并在 300/800/1500ms 三个时点复验根属性 `_NET_ACTIVE_WINDOW`——仍非本窗即补发，复验窗外不再干预（此后用户自行选择焦点是合法态）。连接模式沿 `X11WindowIdentity`/`X11WindowDrag` 先例（独立 XOpenDisplay、restype/argtypes 显式、best-effort 留痕）。

**缩放键双击去抖**：非 mac 绿灯按钮 `onclick` 加 `DoubleClickMs`（500ms，与顶带双击自判同窗）去抖——第二次 click 落在窗口内即吞掉，双击只发一条命令；macOS 绿灯走原生全屏语义不去抖。

**窗口态进页面通道 + 最大化态边缘短路**：`ShellBridgeHost.PublishMaximized` 把最大化态发布进页面（volatile 字段 + `window.__dsh_desk_maximized` 脚本推送）；注入脚本发布同名字段作初值（导航重建文档后随文档销毁，注入即自愈）；`edgeAt` 对该变量整体短路（命中判定与缩放光标一并退出）。宿主侧兜底：最大化态 `BeginResize` 直接吞掉（脱同步期不给窗口系统发无效缩放）。

**取消最大化位置回放（兜底）**：`ShellWindow` 以 `PositionChanged` 追踪常态位置（只在 Normal 态记录）；`Maximized → Normal` 切换时显式回放该值。依据（源码级，2026-10-25 取证）：Avalonia 12.1.4 的最大化/还原是纯 `_NET_WM_STATE` 切换，还原几何全托付 Mutter 的 `saved_rect`；而 X11 窗口的 `saved_rect` 唯一写入点在 maximize 动作内部且有静默跳过守卫（`meta_window_x11_save_rect`：floating 判定/拖拽中不存），跳过时还原到零初始化 (0,0)——真机实证「恢复成小窗但躺在左上角」。无追踪值即跳过，绝不回放 (0,0)；Win/macOS 的 WM 自行还原正确，回放等值幂等无观感差异。

**缩放边日志名与协议对齐**：宿主日志的缩放边从枚举 `ToString()`（`TopLeft`）改为页面协议 kebab 名（`top-left`）——命令帧到命令面的判定位靠日志与协议名对读。

## Alternatives considered

- **X11 事件泵收 `WM_TAKE_FOCUS`（计划书原第 1/2 件）**：源码级证伪——Mutter `src/x11/window-x11.c` 的 `send_icccm_message` 以 `event_mask=0` 发该消息，X 协议下只投递建窗客户端；Avalonia `src/Avalonia.X11/X11Window.cs`（12.1.4）写的 `WM_PROTOCOLS` 只含 `WM_DELETE_WINDOW`/`_NET_WM_SYNC_REQUEST`，其 ClientMessage 分支亦不处理该消息——本壳独立连接永远收不到；且只往 `WM_PROTOCOLS` 加原子而不响应会让 Mutter（`priv->wm_take_focus` 为真即改走应答路径）放弃直落焦点，反而恶化。落败，改道激活请求。
- **退出保留优雅链 + 调看门狗时限**：从未按时走完的拆除链压时限只是把「必然强杀」提前，收益为零；且短时限会在慢机器上误杀正常收尾。落败。
- **去抖做在宿主命令面**（`ToggleMaximize` 收到两条相邻命令吞第二条）：去抖窗内第二次合法点击（顶带双击自判路径）同样会被吞，语义混淆；按钮双击的病灶在按钮 click 监听，就地修。落败。
- **还原位置回放做在 `WindowGeometryStore` 落盘值上**：落盘值在态切换瞬间采集，此时 X11 ConfigureNotify 是否已到不确定，可能存进最大化几何；内存态 `PositionChanged` 追踪只记常态、时序确定。落败。
- **最大化态也发布全屏/最小化等全量态进页面**：当前页面判定只消费最大化一处（`data-fullscreen` 静态不发是上游 CSS 语义，运行时状态已有宿主侧口径）；全量通道无消费方，YAGNI。落败。

## Consequences

- 买到的：退出从「必然 8s 看门狗强杀」变为「回收完成即硬退」（秒级内）；装后重启首映射有激活请求 + 短窗复验两道兜底；最大化态边缘不再吞点击/发无效 resize/显假光标；双击缩放键不再净零；还原位置不再依赖 Mutter 的静默跳过面。
- 付出的：硬退跳过优雅拆除的窗口几何终态落盘与退场内容摘除（代价见 Decision）；焦点兜底的 timestamp 为 CurrentTime，Mutter 焦点窃取防护极严时可能仍吞激活（复验三连发缓解，真机验证项）；位置回放与 Mutter 的取消最大化移动存在竞速可能（单次回放，若真机出现竞速残留按焦点兜底同款有界复验加固）。
- 行为契约豁免一处：`X11FocusBootstrap.Run` 的总 `try` 包整个 X11 交互序列（非「只包一个语句」）——该方法是刻意的粗粒度 best-effort 边界（任一步失败即整体放弃并留痕），与 `X11WindowDrag.Send` 的单 try/finally 先例同型；逐语句收窄只会复制同一段留痕，不改变任何可观察行为。
- 测试：管道记序 ×8（Quit/Restart/幂等/共享 once-guard，hardExit 步骤入序）；脚本回归钉（去抖、态通道初值、edgeAt 短路）。
- 真机验收（2026-10-25，v0.7.5 实机全过）：托盘重启 3s 完成新旧交接、零 SEGV 零 coredump（修复前每次静默 13s+ 由看门狗强杀，已接受的退场噪声实际未发生）；装后自动重启首映射聚焦，`焦点兜底` 首发即中、三次复验零补发，键盘/媒体键/Alt-Tab 可用；双击缩放按钮单翻；最大化还原回原位不落 (0,0)。

## Related

- 计划书：`.plan/修复批-2026-10-25-退出秒杀-焦点兜底-按钮去抖.md`（WM_TAKE_FOCUS 改道已同步标注）。
- 先例 ADR：`2026-10-25-window-identity-geometry-teardown`（几何持久化/摘除管线）、`avalonia-g3-exit-restart`（管道形态）、`2026-10-23-avalonia-caption-bar`（顶栏与拖拽端口）。
