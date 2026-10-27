# Agent Note: 新宿主启动失败面改进程内模态

Status: implemented

Review: FULL/2026-10-23#2/R1=ok R2=ok R3=ok

## Problem

新宿主（Avalonia）从 G0 起没有失败面：`Hosting/ShellStartup` 的启动体抛异常只写 `host.log`，窗口停在 holder 页，用户看到的是一个永远不加载的页面，处置入口只有结束进程。

旧宿主的失败面挂在 Ryn 插件上：`Ryn.Plugins.Dialog` 走外挂工具（Windows `MessageBoxW` / macOS `osascript` / Linux `zenity`|`kdialog`），可用性按 PATH + 执行位探测（`Core.Recovery.DialogToolAvailability`），探不到就回落页内恢复面（`RecoveryPageBuilder` 覆写 DOM）。迁移把 Ryn 退役后，这条通道与它的探测一起消失；而外挂工具在精简桌面/容器里本来就常缺，探测失败分支在新宿主里没有存在必要——模态可以在本进程里画。

两处语义不能混：崩溃恢复面的动作文案是「选『否』继续在后台自动恢复」（`UiCopy.RecoveryDialogActions`），它成立的前提是监督器在重启；新宿主的启动失败没有监督循环（监督在 G3），照抄这句会把「没人重试」说成「正在重试」。

## Decision

- **进程内模态**：`Hosting/FailureDialogWindow`（Avalonia `Window.ShowDialog`，正文 = 可选中滚动的只读文本框，两个按钮：退出 / 留在窗口，Esc 与关闭框等价于留在窗口）＋ `Hosting/InProcessFailureDialog : IFailureDialog`（`IsAvailable` 恒真；属主缺失或建窗异常回 `false`；属主收在托盘时先 `Show()`/`Activate()` 唤回再弹——Avalonia 要求父窗可见，否则模态根本不出现）。
- **处置用例**：`Hosting/StartupFailureFace` 只依赖端口（`IFailureDialog` + 尾部取法 + 退出动作 + 日志），正文构建也在 try 内（构建抛出同样只留痕），headless 可测。**只有显式 `true` 才批准关窗并 Close**——与托盘退出共用 `CloseGate`。
- **正文口径**：`RecoveryDialogText.Build` 的 `exportPath` 可为 `null`（本次不涉及导出，整节省略；不能拿「导出失败」冒充「未尝试」），`actions` 可换。启动失败传 `UiCopy.StartupFailureActions`，其中不含「自动恢复/正在重试」口径（「重新启动应用即可再试」是可执行的事实，不是被承诺的自动行为）。
- **接线**：`ShellStartup` 经 `AttachFailureSink` 交付失败（后台线程触发，先留痕再交付；窗口显示后挂载，早到的失败补交付一次）；`ShellApplication` 在 `Opened` 挂载并 fire-and-forget 调处置用例，模态经 `Dispatcher.UIThread.Post` 建。
- **文案**：新增 `UiCopy.StartupFailureTitle/Reason/Actions`、`DialogStayLabel`；退出按钮复用 `UiCopy.RecoveryExitButton`（同一句话不立两处）。

## Alternatives considered

- **沿用 `Ryn.Plugins.Dialog`**：随 Ryn 退役；外挂工具缺失即无面，且弹窗进程不是本进程，退出动作要跨进程回传。落败。
- **启动失败直接注入页内恢复面（复用 `RecoveryPageBuilder`）**：恢复页按钮走 `desktop.diagnostics.export`/`desktop.recovery.exit` 页面命令，新宿主桥的路由表里还没有这两条；恢复页文案还承诺自动重试，而启动失败没有监督循环，且 DOM 覆写后「留在窗口」没有可回退的页面。落败（随 G3 监督片再做）。
- **失败即静默退出**：窗口消失且无原因，比留下 holder 页更差。落败。
- **复用崩溃恢复文案（`RecoveryDialogActions`）**：把「继续在后台自动恢复」用在没有恢复循环的路径上。落败。
- **模态里自动导出诊断包**：旧宿主在原生承载下自动导出并把路径写进正文（`RecoverySurface` 经 `IDiagnosticsExport`）。新宿主的诊断导出尚未注册进容器，强接会把 Infrastructure 实现拽进装配面（R1）。落败——先省略导出节，随 G3 接回。
- **属主不可见时只留痕、不弹**：进程留在托盘里、用户什么都看不到；唤回主窗是唯一能让失败面出现的路径。落败。

## Consequences

- 启动体失败时模态出现（属主在托盘里则先唤回主窗），正文含原因、stderr 尾部与启动失败动作说明，不含「自动恢复」口径。
- 用户选退出：先批准 `CloseGate` 再关窗；选留在窗口或直接关闭：进程与窗口保持；模态通道故障（属主缺失/建窗异常/取消）只留痕，不退出。
- 崩溃恢复路径（旧宿主 `RecoverySurface`）行为不变。
- **模态无 headless 测试宿主**：机器证据只到正文构建、处置用例与 `ShellStartup` 的晚挂载交付；`ShowDialog` 在各平台窗口管理器下的表现未验【推断 · 未证】。
- **一次失败一条模态**：启动体只跑一次，故不设去重；若将来 G3 把监督循环接到本面，需要「一周期一次」的闩（旧宿主 `_failureSurfaceShown`）。
- **调度器停止残余风险**：`Dispatcher.UIThread.Post` 在 Avalonia 关停后可能不再执行，此时模态不会出现、
  `ConfirmAsync` 的 TCS 永不完成。生产调用点传 `CancellationToken.None`，取消兜底不会触发；该分支只发生在
  退出收尾期，进程即将结束，未再补救【推断 · 未证：Avalonia 关停后 Post 的丢弃行为未实测】。
- **诊断导出缺口**：本片正文没有导出节，用户拿不到一键求助包——G3 接 `IDiagnosticsExport` 注册时补。
- **文案诚实性靠单测钉**：`StartupFailureFaceTests` 断言正文不含恢复面口径。

## Testing

- `StartupFailureFaceTests`：退出只挂显式 true、留在窗口/不退出、通道故障只留痕、取消不退出、英文分支（含「不含自动恢复文案」断言）。
- `ShellStartupTests`：启动体抛出后经 `AttachFailureSink` 交付原异常。
- `RecoveryDialogTextTests`：`exportPath:null` 省略导出节、自定义动作行替换恢复口径。
- 未覆盖：Avalonia 模态的外观与按钮行为（`FailureDialogWindow` 无 headless 宿主）——列入真机验收：启动失败时模态出现、正文可复制、退出经闸门关窗。
