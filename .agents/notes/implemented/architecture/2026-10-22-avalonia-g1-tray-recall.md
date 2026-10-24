# Agent Note: Avalonia G1 首批——托盘、关窗到托盘与二启唤回

Status: implemented

Review: FULL/2026-10-22#2/R1=ok R2=ok R3=ok

评审：首轮 R1 1B+10S、R2 2B+6S、R3 4B+7S；R3 走两轮验轮、R1/R2 各一轮验轮后闭合，Suggestion 逐条书面裁定。

## Problem

G0 只把窗口点亮（[avalonia-g0-entry-switch](2026-10-22-avalonia-g0-entry-switch.md)）：没有托盘，关窗即退出进程；二启仲裁的唤回回调是 no-op，第二实例通知完主实例后自己退出，主实例既不知道也不显示窗口。旧宿主（Ryn）这三件事本来都在：托盘图标与菜单、`CloseGate` 拦关窗隐藏、launcher 二次启动唤回主窗。迁移期用户能直接踩到的差异就是「关一下窗口整个 dsh 没了」和「再点一次图标没有反应」。

## Decision

- **托盘**：`Hosting/ShellTray` 用 Avalonia 自带 `TrayIcon`（`Icons` 附着集合 + `NativeMenu`），菜单本片只有「显示主窗」「退出」，文案取启动时的 UI 语言（语言切换重建随 G3 的 locale 命令路由，桥在 G2 通；本片没有 locale 事件生产者，故不订阅死回调）。只经 `ShellTray.TryCreate` 创建：图标存在性由装配层探测后传入；图标缺失、无会话总线或构造抛异常即返回 null，关窗退回直接退出——托盘是增强能力，不挡退出。Linux 以 `DBUS_SESSION_BUS_ADDRESS` 作前置判据：Avalonia 在缺实现时可能静默建出不可见图标而不抛异常【推断 · 未证】，故隐藏后的恢复通道是「再启动一次即唤回」，真机验收仍是唯一判据。重启与检查更新两项随 G3（退出管道的 Ryn 编排与更新 UI 尚未接进新宿主），旧 `TrayMenuActions.BuildItems` 仍是旧宿主的菜单事实源。`PlatformHasTrayHost` 是静态环境推断（Linux 看 `DBUS_SESSION_BUS_ADDRESS`）：只挡「无会话总线」，挡不住「有总线但无 StatusNotifierItem 宿主」，且在 dbus autolaunch 这类无变量但可用总线的环境会假阴性、静默关掉托盘与关窗到托盘——判据按安全侧取「判不出就不隐藏」，假阴性是该取向的代价【推断 · 未证】。
- **关窗到托盘**：`ShellApplication` 把 lifetime 设为 `ShutdownMode.OnExplicitShutdown`；是否拦截关窗由纯判据 `ShellClosePolicy.ShouldHide(exitApproved, trayReady, platformInitiated)` 决定（未批准退出、托盘可用、非平台发起 → 取消关窗并 `Hide()`）。平台注销/关机（`WindowCloseReason.OSShutdown`/`ApplicationShutdown`）一律放行，且不再重复 `Shutdown()`（平台已在自己的收尾流程里）。应用侧真正结束只走两条路：托盘「退出」（`ApproveExit` → `Close`）或托盘不可用时的直接关窗（平台注销/关机另有放行旁路）；`Window.Closed` 解挂唤回单点、释放托盘，再按非平台发起补一次 `Shutdown()`。
- **二启唤回**：`LauncherActivation.TryBecomePrimary` 增加 `onShowRequested` 参数，`PreBuildRegistration` 透传；`AvaloniaHost.Run` 传入 `ShellRecall.Request`。`ShellRecall` 是挂起/重放单点——首次二启通知可能早于建窗（仲裁先于 `AppBuilder`），未挂窗口时记挂起，窗口就绪时补发一次；此后即投即达，经 `Dispatcher.UIThread.Post` 回 UI 线程。托盘唤回走同一单点；窗口关闭时 `Detach()` 解挂，迟到请求不再伸向已销毁窗口。
- **窗口侧动作**住 `ShellWindow`：`HideToTray` 记隐藏前是否最大化，`RecallFromTray` 还原最小化、显示、置前并按样本恢复最大化后消费样本（沿旧 `TrayRecallMaximize.ShouldEnsure` 判据）。与旧宿主不同，本片没有 Linux「隐藏态预置最大化」那一拍：Avalonia 的 `WindowState` 在 Hide/Show 之间保持，预置拍针对的 GTK 未映射窗口行为不适用【推断 · 未证：真机首唤是否闪变未验】。
- `CloseBehaviorPreference`（用户可选「关闭即退出」）本片未消费：新宿主一律「有托盘则隐藏」，偏好接回随 G3 的命令路由。
- 旧 Ryn 托盘栈（`Tray/`）自 G0 起运行期不可达，但 `CloseGate` 与 `TrayRecallMaximize` 被两个宿主共用——迁移收尾清理 `Tray/` 时这两个类型是共享资产，不能按「旧宿主专属」一并删；`icon.png` 路径字面量也在两宿主各一份，收口时一并合并到常量。

## Alternatives considered

- **复用 Ryn 的 `TrayController`/`Ryn.Plugins.Tray`**：那套控制器与 `IRynWindow`、Ryn DI 容器、companion 事件中继绑死，Avalonia 宿主没有这些面；强行接等于把 Ryn 的托盘栈拖进新宿主。落败。
- **二启不唤回，只保证不重复拉起**：G0 现状，主实例停在托盘里没人知道怎么叫回来，与旧宿主体验直接落差。落败。
- **二启由第二实例再开一个窗口**：会绕过单实例语义并让两个进程抢同一 dsh 运行时。落败。
- **本片直接补齐四项菜单（含重启/检查更新）**：退出管道与更新 UI 仍是 Ryn 编排，接进来要连带搬 `ExitPipeline`/`UpdateCoordinator` 的页面通道，超出本片范围且会把 G3 的决定提前锁死。落败（菜单留两项目，缺口记在 Consequences）。
- **在 `Closing` 里等待隐藏完成（async void）**：关窗必须同步给 `Cancel` 判定，异步等待会把「已取消但窗口已关」的竞态引进来。落败。
- **关窗判据内联在事件处理里**：本片唯一用户可见的行为变更会失去 headless 测试面（`ShellApplication` 无测试宿主）。落败，抽成 `ShellClosePolicy` 纯判据。

## Consequences

- 关窗行为与旧宿主对齐（有托盘则隐藏、无托盘则退出）；托盘不可用时明确降级并留日志，不静默。
- 用户可感知的差异：菜单缺重启/检查更新两项（G3），且「关闭即退出」偏好暂不生效（旧宿主按 `HideOnClose` 直退，新宿主一律隐藏）——两者都随 G3 接回。
- 二启唤回只显示窗口，不重建已崩溃的 dsh 运行时（监督在 G3）。
- Linux 托盘依赖桌面 StatusNotifierItem（GNOME 需扩展）；无该环境时 `TrayIcon` 是否抛异常未真机取证【推断 · 未证】，可能静默建出不可见图标而 `TryCreate` 仍返回实例；真机验收（托盘可见性 + 关窗直退）是唯一判据。
- 本片无实机托盘取证：托盘可见性、菜单点击、`Clicked` 唤回只能真机验；机器证据限于构建、单元测试（`ShellRecall` 六条、`ShellClosePolicy` 五条）与门禁。
- 覆盖率随本批变化：数字的机器家是 `scripts/test-baseline.json`（本批按本地全量 n=1【探索性】预填，push 后按 CI cobertura 跟值），本篇不复制数值。

## Testing

- `tests/…/Hosting/ShellRecallTests.cs`：建窗前请求挂起并在 `Attach` 时补发恰一次、重复挂载不重放、就绪后即投即达、`Detach` 后不再触达旧 target 且不重放陈旧挂起，外加 Request/Attach 并发用例（50 轮，断言恰好投递一次）——覆盖挂起重放与锁竞争两个时间序面。
- `tests/…/Hosting/ShellClosePolicyTests.cs`：关窗裁决五例（托盘就绪×退出批准×平台发起）——「行为级变更必须配套回归」的落点。
- 托盘装配与真实关窗没有单测：本仓未引入 Avalonia headless 宿主，托盘图标还需要真实 StatusNotifierItem；装配处靠评审与真机验。
- 真机验收项（未做）：托盘图标可见、菜单「显示主窗/退出」、关窗隐藏、二启唤回、无 StatusNotifier 环境下的关窗直退、唤回后最大化是否闪变。

## Related

- [avalonia-host-migration](2026-10-22-avalonia-host-migration.md)：迁移阶段表；本片是 G1 的首批。
- [avalonia-g0-entry-switch](2026-10-22-avalonia-g0-entry-switch.md)：上一片。
- [shell-tray-hide-to-tray](2026-08-24-shell-tray-hide-to-tray.md)：旧宿主托盘与 `CloseGate` 语义的出处。
- [single-instance-launcher-activation](2026-08-26-single-instance-launcher-activation.md)：二启仲裁与唤回协议。
