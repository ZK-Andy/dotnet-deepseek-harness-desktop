# Agent Note: services-umbrella-retirement（去 Services 伞，域目录挂根、目录即命名空间）

Status: proposed

## Problem

三项目解组织（[official-clean-architecture-adoption](../../implemented/architecture/2026-09-14-official-clean-architecture-adoption.md)，2026-09-14）完成了**层间**组织，但**层内**文件夹组织未在其范围：单项目时代的巨型 `Services/` 伞目录被一比一搬进新家——Desktop 壳伞内 16+6（Tray）+2（Update）件、Core 12+6 件、Infrastructure 35+7 件。伞目录正是官方指南点名的"按技术角色命名的目录退化为倾倒场"形态（eShopOnWeb 的层内组织为 Catalog/、System/ 等特性/关注点目录，无 Services 伞）。

三条实锤信号：

1. **目录与命名空间已错位**：`Infrastructure/Services/Update/*.cs` 与 `Core/Services/Update/*.cs` 的命名空间已是 `…Infrastructure.Update` / `…Core.Update`（无 Services 段）——代码已决定命名空间形态，目录未跟上，改 Update 域文件须穿越错位。
2. **自发析出轨迹**：Update/、Tray/ 已从伞内析出，说明按域归拢的增长压力真实存在，伞只会继续裂。
3. **规模**：Infrastructure 单目录 35 个适配器平铺，导航与边界审视成本随文件数线性增长。

严重级定性：纯组织债（无运行时/编译正确性风险），与组合根问题（不变量风险）不同量级；但错位信号（第 1 条）意味着本批是"让目录追上既有决定"，不是新决策。

## Proposal

**去 `Services/` 伞，域目录直接挂项目根，目录即命名空间 1:1**（官方 FDG 惯例）；**单文件不成域**——孤件留在项目根（根命名空间），不为单文件开目录。时机与形状（2026-09-15 拍板）：

| 决策点 | 定案 |
|---|---|
| 时机 | **组合根值流管线（composition-root-value-flow-pipeline）四批次之后**，独立机械批次——值流批次的新服务直接落重组后目录，避免先迁进伞再搬出伞 |
| 批次性质 | 纯机械：`git mv` + namespace 行 + using 收敛 + JsonContext 帧类型随域联动；批内禁改逻辑，diff 全是移动/namespace |
| 命令路由归属 | **按特性随域走**（2026-09-15 讨论拍板）：路由器与其服务的特性同目录 |
| 壳根孤件路由（Autostart/CompanionLocale） | 留项目根（单文件不成域，单文件目录是噪音） |
| Infrastructure `Runtime/` | 单目录一步到位（dsh 进程宿主这一单一外部边界 ~16 件可控）；日后超 R4 观察线再析出 `ProcessHygiene/`（reaper 系，析出点天然清晰） |
| System/ 目录命名 | 沿 eShopOnWeb Infrastructure `System/` 先例（Autostart/浏览器/脱敏/诊断导出等杂项关注点） |

目标树（逐文件映射与 tests 镜像同步见实施计划 `.plan/Services伞目录重组-实施计划-2026-09-15.md`，本地工作文档）：

- **Desktop**：`Tray/`（6，已存在）、`Update/`（2，已存在）、`Bootstrap/`（4：两路由+两帧）、`Recovery/`（恢复页三件套+诊断路由，ADR diag-masking-and-recovery-page 同域）、`PageBridge/`（PagePump/PageHealthMonitor/DesktopBanner/RynNavigationCallbacks/AppJson/ExternalLinkCommandRouter——B3 已定形"组合根+UI 桥"，外链路由与导航回调同域）；根留 Program.cs、DesktopBootstrap 分部、RuntimeSupervisor、两个孤件路由。
- **Core**：`Update/`（6，现状即达）、`Plugins/`（3）、`PageHealth/`（2）、`Localization/`（UiCopy/UiLocale）；根留 ExitOrchestration、RuntimeLineage、ExternalLinkPolicy、PreinstallGate、ProfilePackageCheck。
- **Infrastructure**：`Update/`（7，现状即达）、`Runtime/`（~16）、`CliShim/`（3）、`Plugins/`（7）、`System/`（~9）。

配套：tests 三工程按域镜像同名目录；`architecture-standards.md` 命名空间条款（"主工程 Presentation 类型居 `…Desktop.Services*`"）改为"`…Desktop.*` 随目录"；三个 JsonContext（`AppJsonContext`/`UpdateJsonContext`/`InfrastructureJsonContext`）帧类型随域搬位置，**帧形状/协议不变**，仅 namespace 源生成注册联动核对。

## Alternatives considered

- **保留 Services 伞、伞内渐进析出（方案 B）**：diff 最小，但伞本身是无信息量中间层（官方点名形态），且 Update 域命名空间已去 Services 段——保留伞即永久维持目录/命名空间错位。落败：一步对齐官方形态，命名空间迁移是编译期可验证的机械活。
- **壳分部挪 `Bootstrap/` 目录**：非官方解（dotnet/runtime 大类型惯例即 dot 后缀 partial 平铺于类型命名空间目录），且已在 composition-root-value-flow-pipeline 批次 3 定终态。落败，不并入本批。
- **集中 `CommandRouting/` 目录**：与"按特性随域走"拍板冲突——按技术角色集中路由器正是官方点名的退化组织；且跨域浏览路由清单的收益被域目录一层的可发现性抵消。落败（孤件路由因单文件不成域例外留根，不为其开伞）。
- **`Runtime/` 现在细拆 `ProcessHygiene/`**：进程回收/环境治理与 dsh 宿主是同一外部世界的强耦合面（reaper 依赖 host 的 spawn 契约），现阶段 ~16 件可控；预拆引入边界决策成本而无收益。落败，留 R4 触发的析出点。
- **伞更名（`Components/` 等）**：换名字不换本质，仍是技术角色伞。落败。

## Acceptance criteria

- 三工程目录树无 `Services/`；域目录与命名空间 1:1（含 JsonContext 帧类型）；孤件留根无单文件目录。
- 全部 diff 为文件移动 + namespace/using 行 + 源生成注册联动；零逻辑变更；`dotnet test` 全绿（基线 614 起）、0 警告、format 绿。
- tests 三工程镜像同步；`architecture-standards.md` 命名空间条款与现状描述同步。
- 排序承诺兑现：本批在 composition-root-value-flow-pipeline 批次 3 之后执行。

## Risks

- **git blame 连续性**：大批量 `git mv` 后 `git log --follow` 可追溯，但跨 move+rename 的行级追溯可能断——机械批次单批提交可缓解。
- **namespace 全局改名 churn**：机械但面广；以 dotnet format 与编译期验证兜底。
- **JsonContext 源生成注册**：帧类型 namespace 变化须核对 AOT 源生成与消费方 using；帧形状/协议本身不变（协议常量固定），仅类型地址变更。
- **与值流批次的落点耦合**：值流批次 1 的新服务落点按现行结构写，本批紧随执行避免二次搬迁——两战役间不得插入无关变更。

## Related

- [composition-root-value-flow-pipeline](./2026-09-15-composition-root-value-flow-pipeline.md)（proposed）：排序前置；其批次 1 的新服务落点与本批目标树对齐。
- [official-clean-architecture-adoption](../../implemented/architecture/2026-09-14-official-clean-architecture-adoption.md)（implemented）：三项目分层出处；本 ADR 处理其范围外的层内组织。
- [docs/architecture-standards.md](../../../../docs/architecture-standards.md)：命名空间条款待本批同步的单一事实源。
- 实施计划：`.plan/Services伞目录重组-实施计划-2026-09-15.md`（本地工作文档，逐文件映射、批次步骤与验证命令）。
