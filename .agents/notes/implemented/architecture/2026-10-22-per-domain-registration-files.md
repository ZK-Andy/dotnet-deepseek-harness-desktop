# Agent Note: 壳级命令注册按域下沉

Status: implemented
Review: FULL/2026-10-22/R1=ok R2=ok R3=ok

## Problem

壳工程的域目录是 `Bootstrap/`、`PageBridge/`、`Recovery/`、`Tray/`、`Update/`，每个域目录自带实现文件与 `*Registration.cs` 注册文件；注册纪律闸（`scripts/verify-registration-discipline.py`）以文件名后缀认定注册点，因此「注册文件在域目录里」本身即可被机器识别。

[shell-router-rehoming](2026-10-21-shell-router-rehoming.md) 把五个宿主命令路由的实现按域归位后，这五个路由的唯一注册点仍是工程根的 `ShellCommandRegistration.cs`，其五个扩展跨两个域：

- `AddExternalLinkRouting`——外链导航回调与 `ExternalLinkCommandRouter`（`PageBridge/`）；
- `AddLocaleCommands`——`CompanionLocaleCommandRouter` 与 `UiLocaleCommandRouter`（`PageBridge/`）；
- `AddCompanionReportCommand`——`CompanionReportCommandRouter`（`PageBridge/`）；
- `AddAutostartCommands`——`AutostartCommandRouter`（`PageBridge/`）；
- `AddAppRestartCommand`——`AppRestartCommandRouter`（`Bootstrap/`）。

它的聚合判据是「命令谁发起」（页面与 companion 发来的命令归一处），而不是壳工程的组织原则「目录即命名空间、按能力域归位」（[services-umbrella-retirement](2026-09-15-services-umbrella-retirement.md)）。代价三层：

1. 同一域的实现与注册分居两处——`Bootstrap/BootstrapRegistration.cs` 只注册引导重试，同目录 `AppRestartCommandRouter` 的注册在根文件；`PageBridge/` 的四条路由在域目录里没有注册面，读者按目录找不到它们如何进容器。
2. 壳根因此仍留一个能力实现型文件，与「根只剩装配面文件（`Program.cs`、`DesktopAdapter*.cs`、`RynOptionsProfile.cs`）」的形态目标未闭环。
3. 该文件是 [shell-router-rehoming](2026-10-21-shell-router-rehoming.md):46 挂账 ① 的唯一载体——它的存在本身就是挂账未清。

严重级定性：纯组织债，无运行时与编译正确性风险，无 wire 变化。父 ADR 已定「① 属另一决策，②③ 属门禁判据扩展」，本笔记处理 ①。

## Decision

五个扩展按命令的宿主能力域下沉到两个既有域目录，根文件已删除，不新开目录：

| 扩展 | 落点 | 承载域 | 判据 |
|---|---|---|---|
| `AddExternalLinkRouting` | `PageBridge/PageBridgeRegistration.cs` | 页面桥 | 页面 ↔ 宿主命令桥（与 `PageBridge/ExternalLinkCommandRouter` 同面） |
| `AddLocaleCommands` | 同上 | 页面桥 | 页面与 companion 的语言上报/拉取 |
| `AddCompanionReportCommand` | 同上 | 页面桥 | 页面降级报障通道 |
| `AddAutostartCommands` | 同上 | 页面桥 | 开机自启的页面设置面 |
| `AddAppRestartCommand` | `Bootstrap/BootstrapRegistration.cs` | 应用生命周期 | 关窗闸门 → 退出管道重启，与 `Core/Bootstrap/AppExitOrchestration` 同域 |

- 新文件 `PageBridge/PageBridgeRegistration.cs`：`internal static class PageBridgeRegistration`，命名空间 `DeepSeek.Harness.Desktop.PageBridge`，usings 与原文件一致（`Core.Ipc`、`Core.Platform`、`Microsoft.Extensions.DependencyInjection`、`Ryn.Core`、`Ryn.Ipc`）；四个扩展的方法体逐字迁入，不重排 DI 注册顺序、不改 `new` 位置（`new SystemBrowserService()`、`new AutostartService()` 仍只出现在注册文件里，R1a 判据不变）。
- `Bootstrap/BootstrapRegistration.cs` 承担第二个扩展 `AddAppRestartCommand`：`AppRestartCommandRouter` 的构造函数实参不变（`TrayController.CloseGate`、`ExitPipeline.Restart(AppRelaunch.SpawnSelf)`、`HostLog.Write`），延迟解析语义（调用期从容器取）保留。
- 根文件 `ShellCommandRegistration.cs` 删除。
- 调用点零改动：`src/DeepSeek.Harness.Desktop/DesktopAdapter.App.cs:45-73` 的 `RegisterServices` 里 23 个 `services.Add*` 调用名与顺序一律不变（csproj 全局 using 已覆盖 `…Desktop.PageBridge` 与 `…Desktop.Bootstrap`），因此注册顺序与命令解析顺序零位移。
- 注册文件 8 处不变：分布从「Bootstrap 4 / Recovery 1 / Tray 1 / Update 1 / 壳根 1」变为「Bootstrap 4 / PageBridge 1 / Recovery 1 / Tray 1 / Update 1」，壳根 0。
- 不做的事：不把四条页面桥命令注册从「一个域文件内的四个扩展」再拆成四个文件（注册文件粒度是域，不是命令）；不顺手收窄任何可见性（`internal-visibility-narrowing` 的独立判据）。

## Alternatives considered

- **保留 `ShellCommandRegistration` 作五域汇总注册点**（落败）：这是改动前的状态，问题即「聚合判据按发起方而非按域」；保留就等于承认壳根可以长期容留能力实现，父 ADR 的「壳根只剩装配面」目标作废。
- **每路由一个注册文件（`ExternalLinkRouting.cs`、`LocaleRegistration.cs`……）**（落败）：注册文件的粒度是域不是命令——四条页面桥命令共用同一 DI 面且同属页面桥，拆四份只增定位成本，还会让「注册文件 = 域的注册面」这一判据退化成任意粒度。
- **把 `AddAppRestartCommand` 也塞进 `PageBridge/PageBridgeRegistration.cs`**（落败）：`desktop.app.restart` 的能力域是应用生命周期，与 `Core/Bootstrap/AppExitOrchestration` 同域；按发起方（页面发起）归位会让域边界在 Core 与壳之间错位，与父 ADR 的归位判据冲突。
- **保留根文件但改为内部聚合器（只调用各域 `AddXxx`）**（落败）：多一层无判据的间接——聚合器要引用两个域的扩展，等于把壳根重新变成域的交叉点，且给「注册点判据」引入第二个例外。
- **只在父 ADR 的挂账里留记号，不执行**（落败）：父 ADR 已明确「① 属另一决策」——挂账的用途是记录未决，不是长期免做；本笔记即该决策的载具。

## Consequences

- 买到：注册点与实现同住一个域目录，按目录即可找到路由如何进容器；壳根不再留能力实现文件，「根只剩装配面」的形态目标闭环；父 ADR 挂账 ① 清偿。
- 付出：注册点粒度从「一根文件」变为「域文件」，新增命令必须判域归属——判据写在 Decision 表里，成本一次性。机器闸只校验「注册在 `*Registration.cs`」，不校验域归属，若某条页面桥命令实际属应用生命周期（或反之），注册会再次错位；这一点留评审（R1/R3 检查项）。
- 实测读数：`dotnet build` 0 警告 0 错误；`dotnet test` 237+234+6+616 = 1093/1093（基线未动）；`verify-registration-discipline --enforce`（基础设施静态面 3/11）、`verify-compose-root --enforce`（root new-of-owned 3/6、C7 24 项、C8 49 项）、`verify-code-conventions --enforce` 全绿，R1a/R1b 计数未增。
- 评审档：本批触及 `compose-root`（壳根文件集合）与 `behavior-surface`（`DesktopAdapter.App.cs` 注释一行），按 FULL 走 R1/R2/R3，证据行在本笔记头区。

## Related

- [shell-router-rehoming](2026-10-21-shell-router-rehoming.md)（implemented）：挂账 ① 的出处；本笔记清 ①。
- [domain-file-inventory-gate](../process/2026-10-22-domain-file-inventory-gate.md)（implemented）：同批清挂账 ②③。
- [assembly-surface-terminology](../process/2026-10-22-assembly-surface-terminology.md)（implemented）：同批的治理词汇批次。
