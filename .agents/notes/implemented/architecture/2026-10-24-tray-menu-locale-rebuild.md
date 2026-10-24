# Agent Note: 托盘菜单随 UI 语言重建

Status: implemented

Review: FULL/2026-10-24#4/R1=ok R2=ok R3=ok

## Problem

ADR [host-ui-locale](../feature/2026-08-28-host-ui-locale.md) 早已决定「`UiLocale.Changed` 订阅 → 重建托盘
菜单」，落点是旧宿主的 `Program` 接线调 `TrayService.SetMenu`。G5 删除旧宿主后该落点不存在了：新宿主
`ShellTray.TryCreate` 只在创建时按当时 locale 造一次菜单，之后没有任何订阅。结果相同——dsh 运行中切
语言后，菜单（显示主窗 / 检查更新 / 重启 / 退出）停在旧语言，直到重启进程才跟上；README 双语此前按
现状写明「运行中切语言后的菜单重建尚未接」。本篇不重述「订阅事件重建菜单」这个决定，只把它的新宿主
落点接回去。

## Decision

- **订阅落在托盘自身，经 `TrayMenuRebuild`**：它只做「`UiLocale.Changed` → 调一次重建回调」与退订，
  不碰托盘框架对象、不假设回调线程（`Tray/` 域保持无 Avalonia 依赖，与 `TrayMenuActions` 同层）。
- **构建与挂载都在 UI 线程**：`ShellTray.ScheduleMenuRebuild` 把整段重建投递 `Dispatcher.UIThread.Post`，
  投递体内先查释放标志、再 `BuildCurrentMenu()` 并赋给 `TrayIcon.Menu`。事件来自页面桥线程，把「构建」
  也留在投递体内有两个理由：原生菜单只在 UI 线程换装；构建异常（模型漂移）不落回
  `CompanionLocaleCommandRouter` 的命令路径，而是收成一行日志、保留旧菜单。
- **重建与首建共用唯一入口**：`BuildCurrentMenu()` 是 `BuildMenu(uiLocale, recall, checkUpdate, restart, quit)`
  的唯一调用点，首建（`TryCreate` 内、UI 线程同步）与运行期重建都走它，菜单参数增删只改一处。
- **释放即失效**：`ShellTray.Dispose` 先置 `_disposed` 再 `TrayMenuRebuild.Dispose()` 退订；已投递但晚于
  释放的投递体在开头复查标志后直接丢弃，不再碰 `TrayIcon`。构造中途失败（菜单构建/注册抛错）走同一个
  `Dispose` 回收已建绑定，订阅不会漏到进程结束。
- **托盘不可用不建绑定**：`TryCreate` 返回 null 的路径（无图标/无会话总线/构造失败）不订阅事件，关窗
  直退语义不受影响。

## Alternatives considered

- **在 `CompanionLocaleCommandRouter` 里直接改菜单**：页面桥的 locale 路由要拿到托盘对象（或反向回调），
  把 UI 装配细节塞进命令路由；托盘菜单的生命周期也不再由托盘自己管。落败。
- **语言变化时销毁并重造 `TrayIcon`**：菜单能换，但图标会在托盘中消失再出现（部分桌面环境还伴随位置
  重置与一次 StatusNotifierItem 注册），为一个文案变化付出可见闪烁。落败。
- **轮询 `UiLocale.Current`**（定时器或每帧对比）：事件已经存在，轮询只是把失效窗口换成常数开销。落败。
- **在 `Changed` 线程构建菜单、只投递挂载**：少一次 UI 队列上的构建耗时，但构建异常会从事件源
  （页面桥命令路径）逃出去，且把 Avalonia 菜单对象的构造放到非 UI 线程属未验证面。落败。
- **保留缺口，重启进程才换语言**：实现成本为零，但设置区切换语言是常见操作，菜单语言与页面语言长期
  不一致属于可见缺陷。落败。

## Consequences

- 运行中切语言后，菜单文案在下一次 `Changed` 事件的一次 UI 投递后更新；位次、动作映射与「检查更新」
  可见性不变（同一份 `TrayMenuActions` 模型）。
- 重建是异步投递：事件线程不等换装完成；托盘已释放则丢弃，构建/挂载失败只留痕
  （`[host] 托盘菜单重建失败`）并保留旧菜单。
- 托盘图标对象与可见性不因重建重置（换的只是 `TrayIcon.Menu`）。
- 语言切换后的菜单外观属真机面（本机无桌面环境），随托盘验收项一并核对。

## Testing

`ShellTrayActionTests`：语言变化触发一次重建回调、同值上报不触发、释放后再变化不触发、英文 locale 下
菜单逐位等于英文模型；既有 `BuildMenu` 用例继续钉住菜单模型与原生菜单的位次一致。`ShellTray.TryCreate`
依赖 `Application.Current`/`TrayIcon`，托盘对象本身不单测；投递体与释放竞态靠 `ScheduleMenuRebuild` 的释放复查收口，随托盘真机验收核对。
