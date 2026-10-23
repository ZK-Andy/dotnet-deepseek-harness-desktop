# Agent Note: Avalonia G3 首批命令面——自启 / 关窗到托盘 / 诊断导出

Status: implemented

Review: FULL/2026-10-23#3/R1=ok R2=ok R3=ok

## Problem

G3 的目标状态是命令面十二条路由齐（[avalonia-host-migration](../../proposed/architecture/2026-10-22-avalonia-host-migration.md) 阶段表）。G2 只把页面桥域四条
（外链、UI 语言、伴生语言、伴生报障）接进了新宿主，窗口命令由顶栏片追加；旧宿主的
`AutostartCommandRouter` / `CloseToTrayCommandRouter` / `DesktopDiagnosticsCommandRouter` 注册在 Ryn 容器里，
Avalonia 入口没有容器，也没有对应端口装配——伴生插件「桌面」设置区的开机自启开关、关闭到托盘开关、
「导出诊断信息」按钮在新宿主下全部落空（`client.js` 调 `desktop.autostart.getState` 等命令，壳侧无路由即报错）。

关窗侧还有一处纯缺口：`ShellClosePolicy` 只看「托盘就绪」，不读 `desktop.closeToTray` 偏好——
用户在设置里关掉「关闭时最小化到托盘」后，关窗仍会被隐藏，开关是假的。

## Decision

- **三条路由 + 两个装配期单例收在新注册文件** `Hosting/ShellCommandRegistration.cs`：关窗偏好
  （`CloseBehaviorPreference`，`<DSH_HOME>/desktop-preferences.json`）与诊断导出器（`DiagnosticsExportService`，
  Infrastructure 实现）在此构造——基础设施具体类型的 `new` 只许出现在注册文件（R1a），`AvaloniaHost`
  只调用并传递。
- **关窗偏好经 Core 端口**：新增 `Core.Platform.ICloseBehaviorPreference`（`HideOnClose` / `Set`），
  `CloseBehaviorPreference` 实现它；窗口类与 `desktop.closeToTray` 路由只认端口，文件路径、缺文件/
  损坏回退默认 true 的容错留在实现侧（R3 边界；与 `IAutostart`、`IDiagnosticsExport` 同形）。
- **关窗判据读偏好**：`ShellClosePolicy.ShouldHide` 增 `hideOnClose` 参数，判据变为
  「平台未发起 ∧ 未批准退出 ∧ 托盘就绪 ∧ 偏好隐藏」。缺省偏好 true，存量行为不变；显式关掉后关窗直退。
  判据仍是纯函数，四个输入各自可单测。
- **诊断快照经桥端口暴露**：`ShellBridgeHost.HealthSnapshot` 转发内部 `PageHealthMonitor.Snapshot`
  （桥未接上时为 null）。诊断导出器以 `Func<string?>` 在调用期取快照，与旧宿主「健康快照在调用期
  经容器解析」的时点语义一致——观测未起时 state.txt 仍写导出时间/版本/平台等字段，
  `health` 行记 `n/a`（不落空文件）。
- **托盘就绪按调用期读**：托盘句柄存 `ShellApplication` 字段，`closeToTray` 状态帧的 `available`
  经 `Func<bool>` 读该字段、关窗判据内联读同一字段（无托盘环境客户端据此禁用开关）；路由在托盘创建后加入桥，
  `ShellBridge` 每次分发重读路由快照，晚加入不影响先期调用。
- **范围只到这三条**：余下更新（`desktop.update.*`，3 条）、引导重试（`desktop.bootstrap.retry`）、
  恢复退出（`desktop.recovery.exit`）分别依赖更新 UI、首启引导、恢复页，随各自片接入；应用重启随后
  接入（[avalonia-g3-exit-restart](2026-10-23-avalonia-g3-exit-restart.md)），`desktop.tray.event`
  在新宿主无生产者、只留旧宿主装配面（同上 ADR）。命令面因此从「4 + 窗口」变成「7 + 窗口」。

## Alternatives considered

- **一次性接满十二条**：需要同时落下更新栈装配、退出管道（回收/重启/看门狗）、托盘菜单四项、
  首启引导回接与恢复页——四到五个不相干的寿命与失败面挤进一个评审批，任何一条的缺陷都会盖住其余。
  增量闸（gross 600）也放不下。落败（改为逐片）。
- **把 Ryn 的 DI 容器搬到新宿主**：十二条 `AddXxx` 直接复用；但这些注册的构造参数与调用期解析
  都是 Ryn 运行时对象（`IRynWindow`、`TrayService`、`ExitPipeline` 经容器解析、`Ryn.Ipc` 的路由契约），
  搬容器等于把 Ryn 运行时一起搬回来。落败。
- **关窗偏好不进判据，只让开关写盘**：改动最小；但开关仍然无效，属于把假开关留在 UI 上。落败。
- **新宿主自建「壳服务容器」**：后续片（更新/托盘/退出）都在等一个容器，但容器一旦出现，
  装配面 C3/C5 的读数与 R1 的「注册处」判据都要重写；当前直接注入端口足够小，先按需要接线，
  不预铺容器。落败（本阶段）。

## Consequences

- 伴生插件设置区三项在新宿主下恢复可用；`available=false` 时关闭到托盘开关仍按禁用展示。
- 关窗偏好从此生效：设为「关闭即退出」后，托盘就绪也不再隐藏（平台注销/关机仍直接放行）。
- `ShellBridgeHost` 多一个只读快照属性；诊断包的 `health` 行随观测是否启动而变（未起 = `n/a`），与旧宿主一致。
- 关窗偏好多一层 Core 端口；旧宿主 `TrayController` 仍持具体实现（本批不动旧栈）。
- 命令面余下更新（3 条）、引导重试与恢复退出，随各自片接入；`desktop.tray.event` 在新宿主无生产者
  （[avalonia-g3-exit-restart](2026-10-23-avalonia-g3-exit-restart.md)）。应用重启已接
  （[avalonia-g3-exit-restart](2026-10-23-avalonia-g3-exit-restart.md)）；`ShellCommandRegistration`
  是同域路由的家，后续路由不再散落进 `AvaloniaHost`。
- 无新增包引用、无新平台依赖；Linux 托盘可用性判据未动。

## Testing

`ShellCommandRegistrationTests`：路由覆盖面（六条自有命令可路由，五条外部命令不被侵占）、
closeToTray 状态帧的 `available` 与偏好落盘/读回、诊断导出经端口回传路径。
`ShellClosePolicyTests`：新增「偏好关闭即放行关窗」用例，其余五例补 `hideOnClose` 入参。
端口与实现的一致性由 `CloseBehaviorPreferenceTests`（Infrastructure 侧）与 `CloseToTrayCommandRouterTests`
共同覆盖（同一接口的两个消费面）。
