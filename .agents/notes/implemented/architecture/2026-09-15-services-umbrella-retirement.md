# Agent Note: services-umbrella-retirement（去 Services 伞，域目录挂根、目录即命名空间）

Status: implemented

Review: FULL/2026-09-15/R1=ok R2=ok R3=ok

## Problem

三项目解组织（[official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md)，2026-09-14）完成了**层间**组织，但**层内**文件夹组织未在其范围：单项目时代的巨型 `Services/` 伞目录被一比一搬进新家——Desktop 壳伞内 16+6（Tray）+2（Update）件、Core 12+6 件、Infrastructure 35+7 件。伞目录正是官方指南点名的「按技术角色命名的目录退化为倾倒场」形态。

三条实锤信号：

1. **目录与命名空间已错位**：`Infrastructure/Services/Update/*.cs` 与 `Core/Services/Update/*.cs` 的命名空间已是 `…Infrastructure.Update` / `…Core.Update`（无 Services 段）——代码已决定命名空间形态，目录未跟上。
2. **自发析出轨迹**：Update/、Tray/ 已从伞内析出，说明按域归拢的增长压力真实存在，伞只会继续裂。
3. **规模**：Infrastructure 单目录 35 个适配器平铺，导航与边界审视成本随文件数线性增长。

严重级定性：纯组织债（无运行时/编译正确性风险）；错位信号意味着本批是「让目录追上既有决定」，不是新决策。

## Decision

**去 `Services/` 伞，域目录直接挂项目根，目录即命名空间 1:1**（官方 FDG 惯例）；**单文件不成域**——孤件留在项目根（根命名空间），不为单文件开目录。

| 决策点 | 定案 |
|---|---|
| 批次性质 | 纯机械：`git mv` + namespace/using 收敛 + 源生成帧类型随域联动；批内零逻辑变更 |
| 命令路由归属 | 按特性随域走 |
| 壳根孤件路由（Autostart/CompanionLocale） | 留项目根（单文件不成域） |
| Infrastructure `Runtime/` | 单目录（dsh 进程宿主这一单一外部边界）；超 R4 观察线再析出 `ProcessHygiene/`（reaper 系） |
| Infrastructure 杂项域命名 | **`Platform/`**（Autostart/浏览器/脱敏/诊断导出等）。备选 `System/` 因与 BCL 根命名空间同名被否决，见 Alternatives |
| 组合根分部 | 不动（composition-root-value-flow-pipeline 批次 3 终态） |

目标树：

- **Desktop**：`Tray/`（7，含 `TrayController`）、`Update/`（3，含 `UpdateCoordinator`）、`Bootstrap/`（5：两路由+两帧+`FirstBootUi`）、`Recovery/`（恢复页三件套+诊断路由）、`PageBridge/`（PagePump/PageHealthMonitor/DesktopBanner/RynNavigationCallbacks/AppJson/ExternalLinkCommandRouter——组合根 UI 桥）；根留 Program.cs、DesktopBootstrap 分部、RuntimeSupervisor、Autostart/CompanionLocale 两孤件路由。
- **Core**：`Update/`（6）、`Plugins/`（3）、`PageHealth/`（2）、`Localization/`（UiCopy/UiLocale）、`Bootstrap/`（4：BootstrapStep/RuntimeBootstrapGate/BootstrapSettleGate/IFirstBootBootstrap）；根留 ExitPipeline、RuntimeLineage、ExternalLinkPolicy、PreinstallGate、ProfilePackageCheck、DshWebUrl。
- **Infrastructure**：`Update/`（8）、`Bootstrap/`（`FirstBootBootstrapService`——Core `IFirstBootBootstrap` 端口的唯一实现，属域对齐而非孤件）、`Runtime/`（dsh 宿主与进程治理）、`CliShim/`（3）、`Plugins/`（7）、`Platform/`（杂项关注点）；根留 LaunchOptions。

配套：tests 三工程按域镜像同名目录与命名空间；`architecture-standards.md` 命名空间条款改为「目录即命名空间，主工程类型居 `…Desktop[*]`」；三个 JsonContext（`AppJsonContext`/`UpdateJsonContext`/`InfrastructureJsonContext`）帧类型随域搬位置，**帧形状/协议不变**。

## Alternatives considered

- **保留 Services 伞、伞内渐进析出**：diff 最小，但伞本身是无信息量中间层（官方点名形态），且 Update 域命名空间已去 Services 段——保留伞即永久维持目录/命名空间错位。落败：一步对齐官方形态，命名空间迁移是编译期可验证的机械活。
- **Infrastructure 杂项域沿用 `System/`（eShopOnWeb 先例）**：实现期实测 `…Infrastructure.System` 与 BCL 根命名空间 `System` 同名——项目内 `System.Diagnostics/IO/Net/Runtime.Versioning/…` 限定引用（约 108 处）解析到本域，声明位直接编译失败，且今后任何 `System.*` 限定引用都需 `global::` 前缀。落败，改 `Platform/`。
- **壳分部挪 `Bootstrap/` 目录**：非官方解（dotnet/runtime 大类型惯例即 dot 后缀 partial 平铺于类型命名空间目录），且已由 composition-root-value-flow-pipeline 批次 3 定终态。落败。
- **集中 `CommandRouting/` 目录**：与「按特性随域走」拍板冲突——按技术角色集中路由器正是官方点名的退化组织；孤件路由因单文件不成域例外留根。落败。
- **`Runtime/` 现在细拆 `ProcessHygiene/`**：进程回收/环境治理与 dsh 宿主是同一外部世界的强耦合面，现阶段 ~17 件可控；预拆引入边界决策成本而无收益。落败。

## Consequences

- 三工程目录树无 `Services/`；域目录与命名空间 1:1（含 JsonContext 帧类型）；孤件留根无单文件目录。
- 全部 diff 为文件移动 + namespace/using 行 + 源生成注册联动；零逻辑变更；`dotnet build`/`dotnet test` 全绿（628 用例）、`dotnet format` 绿。
- tests 三工程镜像同步；`architecture-standards.md` 命名空间条款与 [architecture.md](../../../../docs/architecture.md) 现状描述同步。
- R2 另报 `Core/RuntimeLineage.cs` 一处 `<see cref>` 指向 Infrastructure 类型，Core 零项目引用下 cref 不可解析——预存在缺陷（HEAD 同款），非本批引入，按机械批不改动面延后处理。
- 唯一越出纯机械面的编辑：Core.Tests 的 `PageHealth` 两文件加 `PageHealthState` 别名——测试命名空间段 `…Tests.PageHealth` 遮蔽同名枚举类型，别名后语义不变。

## Related

- [composition-root-value-flow-pipeline](2026-09-15-composition-root-value-flow-pipeline.md)（implemented）：排序前置；其批次 1/3 的新服务落点已纳入本批目标树。
- [official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md)（implemented）：三项目分层出处；本 ADR 处理其范围外的层内组织。
- [docs/architecture-standards.md](../../../../docs/architecture-standards.md)：命名空间条款的单一事实源。
- 实施计划：`.plan/Services伞目录重组-实施计划-2026-09-15.md`（本地工作文档）。
