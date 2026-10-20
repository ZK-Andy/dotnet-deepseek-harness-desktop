# Agent Note: 实现细节类型的可见性收窄

Status: implemented

Review: FULL/2026-10-21/R1=ok R2=ok R3=ok

## Problem

2026-10-20 的审计（§6.2 疑似：可见性过宽，非死代码）点出两组挂在公共 API 面上、实际只被内部消费的类型：

- `Infrastructure/CliShim/CliShimRegistrar.cs` 里的 `CliShimSetup`、`ShimWriteAction`、`CliShimPlanner`（以及被 `CliShimSetup` 暴露的 `CliShimFile`）——生产侧只在 Infrastructure 内消费，外部只有测试引用。
- `Core/RuntimeLineage.cs` 里的嵌套枚举 `TreeMembership`（与其返回者 `ClassifyMembership`）——生产侧只在 Core 内消费，外部只有测试引用。

它们是实现细节，却以 public 形态挂在**程序集公共面**上（本仓不作 NuGet 包发布，交付形态是 deb/rpm/dmg/安装器——受众是仓内维护者与未来贡献者）：读 API 面的人会把它们当成「将来要用的公共契约」，从而不敢改内部结构——这正是可见性过宽的实际代价。

审计原话是「缺一步，需产品侧确认这些类型是否计划有第二消费者」（本地审计稿 `.plan/审计报告-功能闭环与架构审计-2026-10-20.md`，§6.2，未入库）。

## Decision

- **六个类型 public → internal**：`CliShimFile`、`CliShimSetup`、`ShimWriteAction`、`CliShimPlanner`（Infrastructure 侧）与 `RuntimeLineage.TreeMembership`、`RuntimeLineage.ClassifyMembership`（Core 侧；后者随前者一并收窄——访问一致性要求：public 方法不得返回 internal 类型）。
- **零行为变更**：实现一行未改，只改可访问性修饰。
- **测试可达性不变**：经既有 `InternalsVisibleTo` 仍可访问（完整授予清单见 Consequences）。

## Alternatives considered

- **保持 public，只补文档说明「预留第二消费者」**（落败）：审计要的是产品判断，不是一句注释；这些类型没有第二消费者计划，注释只会变成新的承诺型负债。
- **直接删掉这些类型**（落败）：它们有真实生产消费者（`FirstBootBootstrapService` 的 shim 注册、`RuntimeLineage` 的收割判定）与真实测试覆盖，删了要重写逻辑。
- **把可见性放开给更多程序集**（落败）：与本批目标相反。
- **等「产品侧确认有第二消费者」再动**（落败·已裁定）：用户 2026-10-20 在本仓拍板「执行审计报告未进行动表的残留」，其中就含本项「可见性过宽待产品侧确认」——即产品侧裁定已由用户给出（收窄）。收窄到 internal 是**可逆**动作（真有第二消费者时改回 public 是一行），比留着 public 等下一次复访便宜。

## Consequences

- 买到的：程序集公共面收窄六个类型；实现细节不再对外承诺形状。
- 付出的：将来若真出现跨程序集消费者，需把对应类型改回 public（仓内一行改动 + 一篇 ADR + 新版发布）；若下游是**仓外程序集**（源码级引用），收窄对他们是源码级破坏——需改回 public 并重编译。本仓交付形态是安装器、无第三方程序集引用这些类型，故今天无实际下游。
- 仓内可见性收窄不影响任何现有调用方：生产调用方都在同一程序集内，测试方经既有 `InternalsVisibleTo` 达成（Core 授予 Infrastructure、Core.Tests、Infrastructure.Tests、Desktop.Tests；Infrastructure 授予 Desktop、Core.Tests、Infrastructure.Tests、Desktop.Tests）。
- **收窄范围以审计点名的六项为界，不声称穷尽**：同剖面（生产侧只在同一程序集内消费、外部只有测试引用）仍有 public 类型未收窄——`CliShimBuilder`、`CliShimPath`（Infrastructure/CliShim），`InstallerDownloader`、`ReleaseMetaClient`、`StalePackagePruner`、`UpdatePlatform`、`FileReadyPersistence`（Infrastructure/Update）。本批不扩到它们：可见性在本仓交付形态（安装器、无 NuGet 包、无仓外程序集引用）下只影响「公共面承诺」的阅读成本、无运行时后果，一次收太宽只会把本批的评审面无谓扩大；同剖面清单按批次逐批推进。

## Testing

- `dotnet build` 0 警告 0 错误（可见性变更最易在这里暴露访问不一致——零警告即无残留不一致）。
- `dotnet test` 全绿，测试数与 `scripts/test-baseline.json` 基线一致（基线与 README 双语徽章同批更新）；`CliShimTests` 与 `RuntimeLineageTests` 血统用例经 `InternalsVisibleTo` 仍可达且仍绿。
- 门禁全绿：`verify-code-conventions --enforce`、`verify-code-health --enforce`、`verify-registration-discipline --enforce`、`verify-adr-format`、`verify-md-links`。

## Related

- [host-boundary-ports](../architecture/2026-10-20-host-boundary-ports.md)——同批的边界端口补齐（端口抽象完备面）。
- [core-purity-boundaries](../architecture/2026-10-20-core-purity-boundaries.md)——同批的 Core 纯度下沉（文件/环境 IO 经端口）。
