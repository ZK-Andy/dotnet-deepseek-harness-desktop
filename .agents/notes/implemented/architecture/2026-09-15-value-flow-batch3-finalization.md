# Agent Note: value-flow-batch3-finalization（值流管线批次 3：收口）

Status: implemented

Review: FULL/2026-09-15/R1=ok R2=ok R3=ok

## Problem

[composition-root-value-flow-pipeline](2026-09-15-composition-root-value-flow-pipeline.md)（implemented，四批次主 ADR）批次 3：批次 1 状态下沉、批次 2 值流管线落地后，组合根剩两处收口项——

- **A 类启动配置仍是裸 bool**：dev 判定与自动隔离结论（`Preflight.IsDev`/`DevAutoIsolated`）在阶段产出与消费点之间逐个流动，环境变量读写留在组合根 `ResolveRuntimeAndDev`。主 ADR 拍板 1 要求 6 个 A 类字段 IOptions 化；其中 4 个（`_bootstrapOptions`/`_bootstrapGate`/`_preinstallGate`/`_bootstrapNeeded`）已随批次 1 的首启引导服务私有化并以 `RuntimeBootstrapOptions`/两闸门类型承载，余下 2 个（`_isDev`/`_devAutoIsolated`）尚未类型化。
- **分部仍为 5 个**：主文件 + App/Lifecycle/Navigation/Startup 四个 dot 分部，分部名承诺的分类学已失守；主 ADR 拍板 4 要求归并到至多一个 dot 分部。

本笔记承载批次 3 的执行定案。

## Decision

1. **A 类配置类型化（拍板「IOptions 式」）**：新增 `Infrastructure/Services/LaunchOptions`——不可变 `sealed record`，含 `IsDev`/`DevAutoIsolated` 与派生 `ApplicationIdFor(baseId)`；`LaunchOptions.Resolve(log)` 单点解析环境标记并在 dev 未显式覆盖 home 时应用仓库内隔离（env 读写从组合根下沉 Infrastructure 外部边界）。`Preflight` 产出由两个裸 bool 收成一个类型化值，消费方按需取用：`UpdateCoordinator` 构造收 `LaunchOptions`，组合根经 `ApplicationIdFor` 派生 ApplicationId、经 `IsDev`/`DevAutoIsolated` 分域单实例 socket 与随包插件安装门。该值是环境派生、单次解析的不可变配置，不是被否决的阶段间 context（边界见主 ADR Alternatives）。壳无 Generic Host，不引入 `IOptions<T>`——类型化配置值 + 构造注入是其等价物。
2. **分部文件终态**：`Lifecycle`/`Navigation`/`Startup` 三个 dot 分部删除，方法归并进 `DesktopBootstrap.cs`（启动主链与阶段编排）与 `DesktopBootstrap.App.cs`（应用装配与后台接线、导航原语）。归并按 R4 ≤400 行/文件约束分配，两文件均在闸内。
3. **文档同步**：`docs/architecture-standards.md` R1 补值流编排/分部终态/扩展加法三条；`docs/architecture.md` 组合根形态与 dev 配置描述随现状更新。
4. **主 ADR 迁移**：proposed → implemented（Status/目录/Proposal→Decision/删验收清单/Problem 回写 32 字段实测口径）。

## Consequences

- `dotnet build` 0 警告；`dotnet test` 628/628（未新增用例——纯结构迁移，批次 0 记序网与 `CompositionRootSequenceTests` 值流形态网全过）；`dotnet format` 与机械化门禁全绿。
- 零行为变更自证：无 env 注入变化（`LaunchOptions.Resolve` 读写同一组变量、同一顺序，隔离副作用同点同值）/ 无子进程 spawn 形态变化 / 无可观察副作用变化；方法在同类的 partial 间移动无编译期差异，`Run()` 语句序不变。
- 组合根实例字段保持 10（批次 2 后不变）；分部 5 → 2；`DesktopBootstrap.cs` 340 行、`DesktopBootstrap.App.cs` 346 行。
- 主 ADR 验收全部兑现（编译期缺失产出失败/握手类型化/退出单实例幂等/组合根无空载荷 token）；语言下界残差（exactly-once、异步竞态）仍归宿记序/并发测试，本批不新建并发网。
- 批次 0 延后的测试去重收口：`TestRepoRoot.Find()` 单点替代 `CompositionRootSequenceTests` 与 `ArchitectureTests` 各自逐字重复的仓库根上溯。

## Alternatives considered

- **保留两个裸 bool、只改署名**：A 类配置仍散在阶段产出与消费点，IOptions 化落空。落败。
- **env 读写留组合根、只做 `LaunchOptions` 值类型**：环境交互仍属外部边界直漏 Presentation（R3），解析逻辑继续占据组合根。落败。
- **dev 解析做成 Core 端口 + Infrastructure 实现 + 组合根注入**：解析无外部依赖替换需求（判定纯函数已可单测），为一个启动调用引入接口层是空抽象。落败。
- **把 `LaunchOptions` 注册进 DI 容器**：无 DI 构造的消费方（唯一服务消费方 `UpdateCoordinator` 由组合根按 Run 顺序构造），注册即死代码。落败。
- **保留 4 个 dot 分部**：分部名分类学已失守、无机制约束新方法落点，正是主 ADR 要解决的问题。落败。
- **归并成单个文件**：组合根合计 685 行，超 R4 ≤400 行/文件闸且无 dot 分部惯例兜底。落败。
