# Agent Note: Avalonia 恢复页、首启引导与崩溃监督接回

Status: implemented

Review: FULL/2026-10-23#1/R1=ok R2=ok R3=ok

## Problem

G3 的命令面与 G1 的窗口面陆续接进新宿主后，新宿主仍缺三块**失败与恢复**能力，且缺的都是「dsh 不可用」时才暴露的路径：

- **首启引导没有启动点**。`RuntimeStarter.StartAsync` 在 `IFirstBootBootstrap.IsNeeded` 为真时只登记 shim 并返回 null（spawn 由引导任务负责），旧宿主在 `RunMainLoop` 里调 `bootstrap.Start(...)` 接上；新宿主没有这一调用，PATH 上缺全局 dsh 的机器会停在降级页，引导页永远不出现。
- **崩溃监督没有任务**。`ExitPipeline` 接到新宿主时 `cancelSupervisor` 还是 no-op 占位（[avalonia-g3-exit-restart](../../implemented/architecture/2026-10-23-avalonia-g3-exit-restart.md) 的范围之外），`Core.Bootstrap.RuntimeSupervision` 没被构造，dsh 子进程退出后无人重启、无人展示失败面。
- **恢复页没有承载**。原生失败面已接（[avalonia-failure-dialog](../../implemented/architecture/2026-10-23-avalonia-failure-dialog.md)），但原生通道故障时本应回落的页内恢复面（`RecoveryPageBuilder` + `RecoverySurface`）仍只挂在 Ryn 侧；恢复页脚本还写死 `window.__ryn.invoke`，新宿主里即使注入也不会生效。

三块共享同一组状态：`IFirstBootBootstrap`（Gate + 落定句柄）、`RuntimeSupervision`（监督循环 + 失败面回调）、`IWebViewNavigator`/`IWindowScriptTarget`（导航与页面注入）、`ExitPipeline.cancelSupervisor`。拆成三批会把这组签名改三次，且监督片必然与恢复页片互相依赖。

## Decision

**把 G1 余项（恢复页）与 G3 的恢复/引导/监督部分合成一片**：恢复页、引导重试、恢复退出、首启引导、崩溃监督一次接线；更新三命令与更新 UI 仍留在 G3 后续片（理由见 Alternatives）。

- **恢复页两宿主共用同一构建器**：`RecoveryPageBuilder` 的命令调用改为先试 `window.__shell`、缺失回落 `window.__ryn`。恢复页覆写的是文档 DOM，两个桥对象都挂在 `window` 上，故覆写不影响通道；照搬旧脚本会在新宿主静默失效。命令面仍只有 `desktop.diagnostics.export` 与 `desktop.recovery.exit` 两条。
- **网页视图可测面补导航与可用性**：`IWebViewSurface` 增 `NavigateAsync` 与 `IsAvailable`（UI 线程封送在 Avalonia 适配器里）；新增 `AvaloniaWebViewNavigator` 实现 `IWebViewNavigator`——到达信号取成功完成的顶层导航，求值回报经 `RynProbeValue.Decode` 解壳。这样 `Core.Ui.MainUiEntry` 与 `RuntimeSupervision` 的收口导航不再需要 Ryn 的 `WebViewNavigator`。
- **首屏引导态落在 holder 页**：未铸币时 `/` 返回的过渡屏（`DshLoopbackLocal.HolderPage`）新增 `dsh-desktop-bootstrap` 监听与「重试引导」按钮（`window.__shell` 调 `desktop.bootstrap.retry`）；不新增计时器（holder 的无计时器约束保持），wwwroot 引导页保留为指南侧文档页。
- **脚本求值目标惰性化**：`AvaloniaWindowScriptTarget` 增 `Func<IWebViewSurface?>` 构造，未挂载即抛 `InvalidOperationException`（与 `PagePump` 重试单点同判据）；`ShellBridgeHost` 暴露 `ScriptTarget` 与 `Surface`，引导进度与恢复面在求值时才取当面的网页视图。`FirstBootUi` 改为接收 `Func<IWindowScriptTarget>`，桥尚未装配时丢弃该帧（进度是通知面，host.log 仍有步骤留痕）。
- **装配接口显式化**：`PreBuildRegistration.CreatePreflight` 增 `Func<IWindowScriptTarget>` 参数，两宿主各自注入（旧宿主 `() => shared.ScriptTarget`，新宿主 `() => bridgeHost.ScriptTarget`）；`ShellStartup.Begin` 增收口回调 `afterStart`——起步体（profile → companion → spawn）正常返回后才启动引导与监督，保持旧宿主「profile 先于引导」「引导先于监督」的偏序。三道闸：起步体抛异常（失败面接管）、未传 `afterStart`、令牌已取消，三者任一成立即不执行；spawn 未给 URL 的降级不算异常，照常进入引导/监督。
- **接线落点**：`Hosting/ShellRecoveryRegistration.cs` 构造 `AvaloniaWebViewNavigator`、`RecoverySurface`（原生 `InProcessFailureDialog` 优先、页内回落）、`RuntimeSupervision`、`MainUiEntry` 与两条路由（`desktop.bootstrap.retry`、`desktop.recovery.exit`），R1a 的 `new` 收在注册文件。`ShellApplication` 在建窗与桥挂载后取用，`window.Opened` 触发起步，起步收口后调 `bootstrap.Start` 与监督循环。
- **退出管道去掉占位**：`cancelSupervisor` 实参为 `ShellRecoveryContext.CancelAndJoin`——先取消监督令牌源与引导任务，再按时限 join 监督循环（join 在本方法内，管道只调用该钩子一次）；`HarnessRuntimeHost.Stop` 的前提是监督器已停。窗口 `Closed` 路径只用 `Cancel`（不阻塞：等待会与恢复面求值争同一条 UI 线程自锁），随后释放桥。引导任务自己 spawn dsh，留着它会把刚回收的树再拉起来。
- **无壳 origin 时整组跳过**：代理绑定失败没有可信页面面，恢复页与监督都无承载；保留启动失败面（`StartupFailureFace`）并留痕。监督不会在没有失败面时空转。
- **更新栈不同批**：`desktop.update.*` 依赖更新 UI 与安装后退出路径，寿命面在「dsh 已就绪」，与本片的「dsh 未就绪/崩溃」不共享状态。

## Alternatives considered

- **恢复页、引导、监督分三片**：每片都能独立评审；但 `ExitPipeline.cancelSupervisor`、`RuntimeSupervision` 构造参数与 `IWindowScriptTarget` 的交付方式会被改三次，且监督片离开恢复页只能弹原生模态，原生故障即无承载。落败（合片，理由见 Problem）。
- **把更新三命令一起接完**：命令面一次补齐；但更新 UI、托盘「检查更新」与安装后退出属于另一寿命面，增量闸 gross 600 也装不下（本片已含引导事件、监督循环、恢复页与测试）。落败（按原计划留给后续片）。
- **恢复页按宿主传通道名（`__ryn` / `__shell` 二选一）**：语义最直白；但 `RecoverySurface` 与 `RecoveryPageBuilder` 要被参数化，旧宿主退役时还要再改一次，而双通道回落只是脚本内一个 helper。落败（回落写法）。
- **给新宿主另写一份恢复页构建器**：不动旧宿主；但两套 HTML/CSS/文案会立刻漂移，且 `verify-ui-copy` 的单一词典约束下没有第二份的必要。落败。
- **把引导/监督启动塞进 `ShellStartup` 内部**：少一个回调参数；但 `ShellStartup` 会持有 `IFirstBootBootstrap`、监督器与页面面，退回第二个上帝对象，且它在窗口构造前就创建、拿不到桥。落败（回调在装配侧）。
- **不等引导落定就启监督**：省一个门控；但引导期宿主无 dsh 进程，`WaitForExitAsync` 立即完成会让监督器空转进恢复循环并覆写引导页（旧宿主已踩）。落败。

## Consequences

- 增量闸豁免：gross ≤ 1050，fanout ≤ 15，packages ≤ 1。本批同批接线（拆开会把同一组签名改两次），豁免只对本批生效；三处 `--exempt` 指针（`.githooks/pre-commit`、`.githooks/pre-push`、`.github/workflows/ci.yml`）从 `2026-10-23-avalonia-caption-bar` 转指本篇，旧篇标记随之失效（旧篇补 Erratum）。
- 新宿主在 PATH 缺 dsh 的机器上会真正进入引导态：首屏是代理 holder 页（未铸币时 `/` 的过渡屏），holder 现在监听 `dsh-desktop-bootstrap` 渲染步骤/失败并给出「重试引导」（经 `window.__shell` 调 `desktop.bootstrap.retry`）；wwwroot 引导页保留为指南侧文档页。引导完成后的铸币/终页裁决复用 `MainUiEntry`。
- dsh 崩溃后监督器按旧宿主同款策略恢复：连续崩溃达卡住阈值才弹失败面（进程内模态优先）；模态承载故障时回落页内恢复页，恢复成功后补一次回代理根的收口导航（页面 DOM 已被覆写）。恢复页「退出应用」与托盘退出同一条「先批准关窗闸门再关窗」契约。
- 引导重试与恢复退出两条路由进桥；命令面余更新三命令（`desktop.update.getState/check/install`）与托盘「检查更新」。`desktop.tray.event` 仍随新宿主退役。
- 无新增包引用、无新平台依赖；`IWebViewSurface` 的实现面只在 Avalonia 适配器与测试假体内，Ryn 侧不受影响。
- 迁移期两宿主对恢复页共用同一 HTML/CSS/文案；旧宿主退役时只需删回落分支（与 g2 篇拒绝的「桥双别名」不是同一层：此处只换恢复页里的调用表达式，不新增第二座桥）。
- 残留面（不作本片补）：更新 UI 与安装退出路径；托盘「检查更新」的反馈面（Avalonia 托盘无通知面）；恢复页/引导页的真机验收（本机与 CI 无桌面环境，见 HANDOFF 待办）；mac x64 与 Windows 真机的长期缺位口径不变。

## Testing

`AvaloniaWebViewNavigatorTests`：导航转发、到达信号只认成功完成且可复位、求值回报解壳、窗口就绪跟表面可用性、惰性脚本目标先抛未就绪后求值。
`ShellStartupTests`：起步正常收口后回调执行、起步体抛异常时不执行、关窗取消不执行。
`ShellRecoveryRegistrationTests`：两条路由进表且不侵占更新命令；恢复退出先批准闸门再关窗；`Cancel`（不阻塞、取消两路）与 `CancelAndJoin`（超时留痕）的退出契约。
`RecoveryPageBuilderTests`：脚本同时带双通道且两个命令 ID 各一次。
全量测试通过、0 警告（用例数与覆盖率以 `scripts/test-baseline.json` 为唯一家，本批待 CI 跟值）；本机 DEBUG 构建不覆盖真机引导/恢复路径（无桌面环境）。到达信号对任何成功完成的顶层导航都触发（与旧宿主同语义），`MainUiEntry` 的提交等待按「已提交」降级，故不引入新的裁决分支。
