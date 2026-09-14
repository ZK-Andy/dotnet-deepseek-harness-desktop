# Agent Note: clean-architecture-b4-finalization（三项目解组织 B4：测试镜像 + 门禁升级 + CPM 收尾）

Status: implemented

Review: FULL/2026-09-14/R1=ok R2=ok R3=ok
Review notes: 三路 0 Blocker；10 Suggestion（死依赖/注释口径/死参数/锚定对齐/CPM 措辞/复认记录/supersession 标注/Related 收口指针）全部当场收口

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

父 ADR [2026-09-14-official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md) B1–B4 路线的收尾批。B3 后三层归属已定形，但四处过渡态未收：55 个测试文件单工程平铺（官方判据 7 FAIL）；A2/A4/A5 仍是 NetArchTest 命名空间前缀扫描（可被同前缀命名绕过）；Core/Infrastructure 命名空间仍是迁移期的 `…Services*`（B1/B2 备选明确「延后到 B4，非取消」）；D005 文件名白名单 40+ 条目（补丁形态本身）与五工程重复 csproj 配置（官方判据 8 PARTIAL）仍在。

## Decision

**四件收尾一次落盘，零行为变更：**

- **CPM + Directory.Build.props（官方判据 8）**：根 `Directory.Packages.props` 收敛原 11 个包版本单一事实源（`NetArchTest.Rules` 随 A2/A4/A5 退役一并摘除，现存 10）；`Directory.Build.props` 收敛五工程同构 PropertyGroup（net10.0/ImplicitUsings/Nullable/分析器强制/AD0001）；各 csproj 只留差异项（OutputType/包集/硬约束注释/InternalsVisibleTo）。
- **tests 三项目镜像拆分（官方判据 7）**：`tests/{Core,Infrastructure}.{Tests}` 新建，主测试工程只剩 Ryn 宿主壳/组合根测试（90/379/145 分布）；混合文件拆类落层——`ConvenienceTests` 三拆（UpdateBanner→主壳、AutostartBuilder→Infrastructure、UpdateInstallFailureRecovery→Core）、`CloseToTrayTests` 二拆（偏好持久化→Infrastructure、路由→主壳）、`PageHealthMonitorTests` 二拆（tracker 判定核→Core、探针 Parse 桥面→主壳）、`RuntimeLineageTests` 拆出 loopback bind 探针三例→Infrastructure、`RuntimeBootstrapTests` 拆出 `BootstrapGateAndRouterTests`（main 类型）→主壳、`RunMarkerTests`/`RuntimeVersionGateTests` 拆出横幅契约例→主壳 `DesktopBannerContractTests`；`DiagnosticsTests`/`BundledPluginCatalogTests`/`PluginVersionCheckTests`/`ReleaseAssetTests`/`DevEnvironmentTests` 等按消费边界落 Infrastructure.Tests。CI 零变更（coverage 走 slnx 全采集；OS 语义腿 `FullyQualifiedName~` 过滤与程序集无关仍命中）。
- **命名空间随程序集升级（B1/B2 延后项兑现）**：Core → `DeepSeek.Harness.Desktop.Core[.Update]`、Infrastructure → `…Infrastructure[.Tray|.Update]`；主工程 Presentation 命名空间保持 `…Desktop.Services*` 不变。消费侧免逐文件翻动：各 csproj 按**依赖方向合法的命名空间白名单**声明全局 `Using`（Core 零外层、Infrastructure 只 Core 系、主工程/测试工程三层全量）。
- **A2/A4/A5 退役 → 项目引用断言**（B3 延后的托盘三类型复认随此收口：`TrayRecallMaximize`/`TrayCheckFeedback`/`CloseGate` 复认维持 Presentation 原位——托盘语义单点判据仍成立，端口化备选继续落败）：`ArchitectureTests` 重写为引用图形状断言两级互证——csproj 声明面（Core 零 ProjectReference、Infrastructure 恰引 Core、主工程恰引 Core+Infrastructure，路径归一化防 OS 分歧）+ 程序集产物面（原 R2 种子测试保留：Core 零 DeepSeek*/Ryn* 引用、Infrastructure 只引 Core）；NetArchTest 依赖摘除。
- **D005 白名单退役**：40+ 条文件名白名单删除；豁免改按工程推导——Infrastructure 整工程豁免（边界层本体）、Core 仅 `ProfilePackageCheck`/`PluginVersionCheck` 两个 B1 既定只读边界文件、主工程零豁免；`verify-code-health` 扫描根扩三工程（`--src` 多值）；self-test 夹具同步改工程模型。

## Alternatives considered

- **命名空间保持 `…Services*` 不再改**：B1/B2 备选已记「延后，非取消」；物理分层完成后前缀扫描门退役，命名错位将永久化且违反 FDG 命名。落败（本次兑现）。
- **主工程命名空间一并改 `…Presentation.*`**：主工程是组合根+UI 桥，`Services*` 子命名空间仍准确描述其命令路由群；改动只增 diff 面无规则收益（A5 门已退役，归属由 csproj 强制）。落败。
- **D005 改 Roslyn analyzer**：B1 备选既定的另立项（自定义 analyzer 试点）；扫描器已够用且本次只是豁免推导方式变更。落败（延后）。
- **测试三工程共享固定装置上提公共工程**：跨工程共享仅 `DshHomeEnvCollectionDefinition`/`TestTarGz`/`ExternalFileLockHolder` 三个文件且全在 Infrastructure.Tests 消费面内，为三文件建第四个工程属过度设计。落败。
- **ArchitectureTests 只留 csproj 断言**：csproj 声明可被生成/覆盖失真，程序集引用面是编译产物的直接证据；两级各 ~20 行，互证成本低。落败（保留双面）。

## Consequences

- 官方判据 1–8 全部 PASS；三项目解组织迁移收官，规范 [architecture-standards](../../../../docs/architecture-standards.md)「强制与迁移」改为完成态表述（机器强制 = 项目引用 > 项目引用断言 > 按工程推导的 D005）。
- **614/614（90+379+145）、0 警告、format 与门禁全绿；零行为变更判据**：源码面 = 命名空间声明/限定名重写 + 全局 Using（编译期改名，无运行时语义）；测试面 = 文件移动与拆类（断言逐条保留，无测试语义变化）；门禁面 = D005 豁免推导方式变更（扫描结果零差异）。
- 新代码落位判据回归直觉：进错工程即编译失败或 ArchitectureTests 红；`using` 纪律由各 csproj 的命名空间白名单约束（越界命名空间全局不可见）。
- 测试徽章基线随标准跟值批收口（612→614、`4040/7306 = 55.30%`；[覆盖合并口径](../testing/2026-09-14-coverage-baseline-multi-project-merge.md)）。

## Related

- [2026-09-14-official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md)：父 ADR；[b1-core-extraction](2026-09-14-clean-architecture-b1-core-extraction.md)/[b2-infrastructure](2026-09-14-clean-architecture-b2-infrastructure.md)/[b3-presentation](2026-09-14-clean-architecture-b3-presentation.md)：前三批。
- [2026-08-30-architecture-mechanization](../process/2026-08-30-architecture-mechanization.md)：A2/A4/A5、D004/D005 原通道（本批升级/退役）。
- [docs/architecture-standards.md](../../../../docs/architecture-standards.md)：规范正文。
