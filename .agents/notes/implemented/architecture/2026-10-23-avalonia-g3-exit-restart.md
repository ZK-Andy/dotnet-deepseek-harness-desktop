# Agent Note: Avalonia G3——退出/重启管道与托盘重启

Status: implemented

Review: FULL/2026-10-23#4/R1=ok R2=ok R3=ok

## Problem

新宿主托盘菜单只有「显示主窗 / 退出」（重启项尚未存在），退出动作是「批准闸门 + `Close()`」：运行时回收
（停 dsh 整树、释放单实例监听器、释放 run-marker）只在 `Run` 的 `finally` 里发生，关窗在先、回收在后，
既没有看门狗兜「主循环不返回」，也没有任何重启入口。`desktop.app.restart` 因此在新宿主下无路由，
companion 设置区的「重启应用」落空；Web 通道失效时托盘菜单本该是唯一存活的重启入口
（ADR `app-restart-native-switch`），而这条入口同样不存在。

同一片核实到一处契约消失：`desktop.tray.event` 的生产者是 Ryn 托盘插件——companion `client.js` 只中继
`window.__ryn.on('tray.clicked' / 'tray.menuItemClicked')`；Avalonia `TrayIcon` 的菜单点击是原生回调，
页面侧没有事件源。为新宿主保留该路由只会得到一条永远不触发的死契约。

## Decision

- **退出管道在装配期构造**：`AvaloniaHost.CreateLifetime` 以回收三步（监督器取消 → `host.Stop` →
  `RunMarker.Release`）、监听器释放与关窗动作构造 `ExitPipeline`，经 `RunUi` 传给 `ShellApplication`。
  关窗动作在调用期取 `desktop.MainWindow` 并投递 UI 线程（建窗晚于装配，托盘/命令面调用必晚于建窗）。
  监督器取消暂为 no-op：监督片之前新宿主没有恢复分支可取消，接监督时必须回填。
- **托盘退出/重启收在 `ShellCommandRegistration.CreateTrayActions`**：两个动作都先 `CloseGate.ApproveExit()`
  再走管道——未批准的关窗会被 hide-to-tray 吞成隐藏，菜单「退出」会变成「藏起来」。回收在线程池执行
  （原生菜单回调在 UI 线程，`Stop` 的门等待与进程回收不占 UI），管道自己把关窗投递回 UI 线程。菜单补
  「重启」项（显示主窗 / 重启 / 分隔 / 退出），仍是原生回调；启动失败面的「退出」与托盘共用同一个 quit
  动作。
- **`desktop.app.restart` 复用既有 `AppRestartCommandRouter`**：受理即回帧、200ms 冲刷延迟后执行，先批准
  闸门再走同一个重启动作；回收与监听器释放先于 spawn（旧 dsh 树不死透会被看门狗击杀新实例，监听器不
  释放新实例过不了单实例仲裁）。
- **spawn 失败仍关窗**：`SpawnGuarded` 把 `AppRelaunch.SpawnSelf` 的异常收成一行日志后返回，管道继续关窗
  并武装看门狗——否则留下 dsh 已死、窗口还在、看门狗未武装的僵尸壳，且管道 once-guard 已置位，此后
  托盘「退出/重启」与 `desktop.app.restart` 全部变成空操作。
- **新宿主不注册 `desktop.tray.event`**：该命令的生产者只存在于 Ryn 托盘插件；旧宿主装配面为正本保留。
- **`spawnSuccessor` 为可选注入口**（`CreateTrayActions`）：默认生产路径，测试注入记序 fake——与
  `ExitPipeline` 既有的 `startWatchdog` 注入口径一致；单测不得真拉起进程。

## Alternatives considered

- **把管道构造塞进 `ShellApplication`**：需要把 `HarnessRuntimeHost`、`RunMarkerResult`、`PrimaryListener`
  三个 Infrastructure 具体对象漏进应用对象（或再穿四个回收委托参数）。边界与参数面都变差。落败。
- **退出保持「批准 + `Close()`」，靠 `Run` 尾部回收**：改动最小；但关窗先于回收、无看门狗，且重启仍需
  另起一条路径，退出语义会出现两个家。落败。
- **给 `desktop.tray.event` 造 Avalonia 事件源**（原生点击 → 注入脚本 → 页面 → 再回调宿主）：页面侧没有
  任何消费者，只为让契约存在而增加一圈往返。落败。
- **重启走 `Environment.Exit` + `AppRelaunch`**：绕过回收；旧 dsh 树与监听器存活，新实例要么被看门狗击杀
  要么过不了单实例仲裁。落败。
- **让 spawn 异常上抛**：管道在 spawn 前已回收并置位 once-guard，异常只会把「退出/重启」变成空操作，
  留下一个 dsh 已死的空壳进程。落败。

## Consequences

- 托盘「退出 / 重启」、设置区「重启应用」与启动失败面「退出」在新宿主下可用且同源；退出序为
  监督器取消 → 停运行时 → 释放 marker → 释放监听器 → 关窗（重启则先拉起新实例）→ 8s 看门狗。
  `Run` 尾部的资源释放仍在关窗后兜底（`Stop` / `Release` / `Listener.Dispose` 均幂等）。
- 窗口「直退」路径（托盘不可用或偏好关闭）不经管道，靠 `Run` 尾部回收——那条路径没有 8s 看门狗。
- 托盘菜单仍缺「检查更新」：等更新栈片。且 Avalonia 托盘没有系统通知面，菜单触发的检查结论届时只能
  进 host.log（旧宿主经托盘通知送达）——缺口随更新片定策，不在本片造半套通知。
- `desktop.recovery.exit` 仍未注册：恢复页未接，路由随恢复页片；命令 ID 继续由旧宿主页面引用。
- 监督器取消是 no-op：重启不取消监督（尚无监督）。接监督片时若不回填，重启会把旧监督分支留给新实例。
- 语言切换后的托盘菜单重建仍未接（locale 命令已在 G2 装配，但改语言不重建菜单）。
- 已知缺口（不在本片；旧宿主同形）：重启时回环代理端口由本进程持有到 `Run` 尾部才释放，继任者若先于
  那次释放绑定，会走「记忆端口被占 → OS 重分配」，本次重启丢一次会话恢复。
- 无新增包引用；`docs/architecture.md` 托盘段与 README 双语迁移段同批更新。
- 增量读数：gross 424 / 既有文件净增 +100 / fanout 7（帽 600 / 150 / 15）。

## Testing

`ShellCommandRegistrationTests`（6 例）：四条命令族的覆盖面与不侵占（更新/托盘事件/引导/恢复/窗口）、
`desktop.app.restart` 的「闸门放行 → 回收 → spawn → 关窗 → 看门狗」次序、托盘退出的批准时序（首个管道
步骤即读到已批准）、spawn 抛错仍关窗的回归、closeToTray 状态帧、诊断导出端口路径。
`CreateLifetime` 的生产绑定（哪台 host / marker / 监听器、关窗投递）属 Avalonia 应用装配，单测不覆盖，
由实跑验收（托盘退出/重启、设置区重启）承担；测试基线数字的唯一事实源是 `scripts/test-baseline.json`。
