# Agent Note: official-clean-architecture-adoption（采纳 .NET 官方 Clean Architecture 三项目解组织）

Status: implemented

Review: FULL/2026-09-14/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

用户 2026-09-14 拍板：对当前架构完全不满意，要求严格按 .NET 官方架构标准落盘并对照审核。审核对照《Architect modern web applications with ASP.NET Core and Azure》第 2 章官方判据与官方参考实现 dotnet/eShopOnWeb（单体主链 `src/{ApplicationCore, Infrastructure, Web}`），测量对象为 2026-09-14 工作树（`find`/`wc`/`grep` 可复现）：

| # | 官方标准条目 | 现状（证据） | 判定 |
|---|---|---|---|
| 1 | 解组织随规模升级多项目（官方点名：单项目+文件夹分层「frequently leads to spaghetti code」） | 单项目 84 .cs / 10,768 行；`Services/` 一个文件夹 58 文件平铺，端口/适配器/用例/纯逻辑混居 | FAIL |
| 2 | 依赖方向编译期强制（分层项目引用） | 物理不可行：仅 NetArchTest 命名空间前缀扫描（机械化门 A2/A4/A5）+ `verify-code-conventions` D005 白名单（20+ 文件手工维护）硬撑 | FAIL |
| 3 | Application Core 隔离（纯逻辑居中、零基础设施依赖） | Core 不存在；纯逻辑与 I/O 同层混居（`UpdateStateMachine` 与 `HarnessRuntimeHost` 同夹层） | FAIL |
| 4 | 端口/适配器：接口定义在 Core、实现在边界 | 全仓接口仅 1 个（`UpdateStateMachine.IPersistence`）/84 文件；Ryn/native、dsh 进程、companion IPC、文件/网络、更新 feed、注册表/rc 各边界零端口；37 文件直触 `Process`/`File`/`HttpClient`/`Registry` | FAIL |
| 5 | 组合根只装配（R1） | `DesktopBootstrap` 5 partial 共 1,375 行平铺项目根（App 206 / 380 / Lifecycle 371 / Navigation 51 / Startup 367）+ `Program.cs` 45 行（合计 1,420）；装配已程序化但承载面偏大 | PARTIAL |
| 6 | 命名（FDG `<Company>.<Component>`）、src/tests 布局、DI 容器 | 命名空间/程序集命名合规；slnx 已分 src/tests 组；MEDI 经 Ryn builder 在用 | PASS |
| 7 | 测试按层组织（Core 单测 / Infrastructure 集成测分界） | 55 测试文件单项目平铺；按组件命名但不分层 | PARTIAL |
| 8 | 解级工程配置收敛（`Directory.Build.props` / 中央包管理） | 仅 `global.json`；多项目化后重复配置面将放大 | PARTIAL（多项目前置项） |

根因（官方原文 + 机制直证，非推断）：2026-08-30 前版规范拍板「采纳原理、不照抄模板」，把多项目模板判为「对该规模属过度设计」——但 84 文件 / 八类外部边界已非官方语境的「小应用」，官方判据（非平凡单体 → Clean Architecture）命中；机械化 ADR 与 `ArchitectureTests` 头注释均自证：R3 边界抽象需接口抽取，保留为评审项、不作硬门禁——单项目形态下依赖方向与边界抽象**永远无法机器强制**，D005 白名单即补丁形态本身。

## Decision

**采用 .NET 官方三项目单体标准（eShopOnWeb 形态）；`docs/architecture-standards.md` 原地重写为规范正文（单一事实源不变）；前版「不照抄多项目模板」条款由本笔记取代。本批只落规范 + 本 ADR + 迁移路线，零代码。**

- 规范要点：官方三项目解组织（目标解组织表、R1–R5 规则见规范正文，单一事实源不变）；R1–R5 编号沿用，其中 R2 升级为编译器强制、R3 升级为物理端口位（接口在 Core、实现在 Infrastructure、组合根注入）；`tests/` 按层镜像（Core 单测 / 边界集成测）。
- 迁移路线（各批独立走 feature-flow，机器定档评审，零行为变更为合并前置；批内 ADR 记录文件移动映射）：
  - **B1 骨架 + Core**：三项目 slnx 骨架；纯逻辑组件移入 Core（`UpdateStateMachine`/`IPersistence` 端口化、`ExternalLinkPolicy`、`RuntimeLineage` 策略核、`PageHealthMonitor`、`PreinstallGate`、`PluginVersionCheck`、`BundledPluginCatalog`/`PresetPluginCatalog`、`UiLocale`、`UpdateState*` 纯核）。
  - **B2 Infrastructure**：dsh 进程族（`HarnessRuntimeHost*`/`RuntimeBootstrap*`/`RuntimeVersionGate`/`PluginProcessRunner`/`DshSubprocessScopeReaper`/`OrphanDshReaper`）、引导与 shim（`CliShim*`/`MarketInstallHelper*`/`DesktopProfileBootstrap`/`PluginProfileTransaction`）、系统面（`SystemBrowser`/`LauncherActivation`/`RunMarker`/`Autostart`/`HostLog`/`SecretMasker`/`DiagnosticsExporter`）、更新集成面（`UpdateInstaller`/`InstallerDownloader`/`ReleaseMetaClient`/`FileReadyPersistence`/`StalePackagePruner`/`UpdatePlatform`）。
  - **B3 Presentation 收拢**：组合根与 UI 桥（`DesktopBootstrap*`、横幅/恢复页、`RynNavigationCallbacks`、托盘桥、命令路由群、`PagePump`/`UiCopy`）。
  - **B4 收尾**：tests 三项目镜像拆分；`ArchitectureTests` 机械化门 A2/A4/A5 升级为跨项目引用断言；D005 白名单退役；`Directory.Build.props` + 中央包管理（官方判据 8）。
  - 每批验收：`dotnet test` 全绿 0 警告 + 门禁全绿 + 「零行为变更判据」逐条对照。

## Alternatives considered

- **维持单项目 + 文件夹（前版立场）**：依赖方向永远只能靠命名空间扫描 + D005 白名单硬撑（白名单即补丁证据），A3/A4 无解；且与用户拍板直接冲突。落败。
- **照抄 Clean Architecture 四项目模板（Domain/Application/Infrastructure/Presentation 全分列）**：官方单体参考实现 eShopOnWeb 即三项目（Application Core 已合并域模型与应用服务）；四分列对本规模属过度切分。落败（采官方参考实现形态）。
- **不拆项目、只把 ArchitectureTests 升硬门禁**：命名空间前缀匹配可被同前缀命名绕过，文件混居照旧；编译器项目引用是唯一不可绕的门禁。落败。
- **规范另立新文档、architecture-standards.md 保持单项目立场**：违反「每个事实只有一个家」，两份规范并立必漂移。落败（原地重写）。
- **本批即动代码拆分**：规范未落地前对 84 文件动刀范围失控（同前版 ADR「先规范后重构」节奏）；拆分按批次走实现模式。落败（延后，非取消）。

## Consequences

- 依赖方向治理从「评审兜底」变「编译器接棒」：R2 越界即编译失败；D005 白名单进入退役轨道（B4 摘除）；A5（新类型必进 Services/）退役为项目归属规则。
- 84 文件跨三项目重排分 B1–B4 四批；迁移期单项目与规范不符是已知过渡态——**新代码一律按目标层落位**，过渡期由 D005 白名单续守。
- 仓内自有接口（`IPersistence`）随层迁移落 Core，八类外部边界在 B2/B3 逐类补齐端口抽象；Ryn SDK 的 `ICommandRouter` 实现属 UI 命令面，留 Presentation 侧按需适配 Core 端口（落 Core 反而违反 Core 零外层引用）。
- csproj/slnx 随批次更新；`dotnet test` 走 slnx 全量不受项目拆分影响，CI 工作流零变更。
- 本批零代码：门禁（verify-adr-format/md-links/doc-budgets/cookbook/handoff/governance）应全绿；AGENTS 评审检查项 R1/R3 表述继续有效（约束对象从「文件夹」变「项目」）。

## Related

- [docs/architecture-standards.md](../../../../docs/architecture-standards.md)：规范正文（本 ADR 落地文档）。
- [2026-08-30-architecture-standards](../process/2026-08-30-architecture-standards.md)：前版规范 ADR（「采纳原理、不照抄模板」条款被本笔记取代）。
- [2026-08-30-architecture-mechanization](../process/2026-08-30-architecture-mechanization.md)：机械化通道（ArchitectureTests 门 A2/A4/A5、D005/D004 现役门禁）。
- [dotnet/eShopOnWeb](https://github.com/dotnet/eShopOnWeb)：官方单体参考实现（主链三项目 ApplicationCore/Infrastructure/Web）。
