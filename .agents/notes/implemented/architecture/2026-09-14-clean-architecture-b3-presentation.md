# Agent Note: clean-architecture-b3-presentation（三项目解组织 B3：组合根与 UI 桥收拢 Presentation）

Status: implemented

Review: LIGHT/2026-09-14/R2=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

父 ADR [2026-09-14-official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md) B1–B4 路线的第三批。B1/B2 迁出后，主工程剩 34 文件；对剩余文件逐个审计（对照 R1「具体基础设施类型只出现在组合根注册处」与「纯逻辑居中」）发现两类滞留者：纯逻辑 `PageHealthRecovery`/`ExitOrchestration` 混居 UI 桥夹层，边界触碰面 `LegacyHomeNotice`（环境探测）/`CloseBehaviorPreference`（偏好文件落盘）直漏在表示层（D005 白名单两处兜底即其证据）。

## Decision

**主工程即 Presentation 层定形：B3 不新建工程，把滞留者迁出使主工程只剩组合根与 UI 桥；B2 遗留的「Ryn 面处置」一并收口——`ICommandRouter` 实现、托盘桥、Ryn 桥按父 ADR 既定立场全部留 Presentation，不端口化。** 命名空间保持不变（B1/B2 先例），文件移动映射：

| 源（主工程） | 去向 | 判据 |
|---|---|---|
| `PageHealthRecovery.cs` | Core | 纯逻辑（有界恢复预算，零 I/O），`PageHealthTracker` 文档 cref 同程序集化 |
| `ExitOrchestration.cs` | Core | 纯序编排（回收三件套 Action 注入，零 I/O）；「用例编排」属 Application Core |
| `LegacyHomeNotice.cs` | Infrastructure | 只读 home 探测边界（`Directory.Exists`/`Environment`），与 B2 `RuntimeLineageProbes` 同类 |
| `Tray/CloseBehaviorPreference.cs` | Infrastructure | 偏好文件持久化适配器（temp+rename 原子落盘）；命名空间保持 `…Services.Tray`（消费方零改动） |

拆解（一处，等价改写沿 B2 先例）：`PreferencesFile` 序列化注册随类型走——`InfrastructureJsonContext` 增注册（CamelCase+Serialization 选项一致，键名 hideToTrayOnClose 不变），`AppJsonContext` 摘除注册；CloseBehaviorPreference 文档引用同步换家。

留守判据（逐类审计结论，防重审）：`DesktopBootstrap*`/`Program`（组合根 R1）；`DesktopBanner`/`UpdateBanner`/`RecoveryPageBuilder`/`BootstrapStateFrame`/`PreinstallFrame`（横幅/恢复页/引导帧注入桥）；`RynNavigationCallbacks`/`PagePump`/`PageHealthMonitor`（Ryn 桥）；`*CommandRouter` 群 + `Tray/*`（UI 命令面，父 ADR 已定留 Presentation）；`TrayRecallMaximize`/`TrayCheckFeedback`/`CloseGate`（托盘桥判定与闸门，虽纯但属托盘语义单点，强迁 Core 只为纯而纯）；`AppJson.cs`（Presentation 帧契约）。`RuntimeSupervisor` 留 Presentation（崩溃恢复接线：恢复屏 UI + Infrastructure 宿主重启的粘合，不含可独立单测的策略核）。

## Alternatives considered

- **`TrayRecallMaximize`/`TrayCheckFeedback`/`CloseGate` 随纯逻辑迁 Core**：三者是托盘桥的判定/闸门单点，与 Ryn 窗口语义强耦合；迁 Core 需把托盘动作面一并端口化，为本批引入新端口超范围。留 Presentation，B4 ArchitectureTests 升级时按层归位复认。落败（延后复认，非取消）。
- **`RuntimeSupervisor` 端口化迁 Core**：监督循环消费 `HarnessRuntimeHost` 具体类型，端口化需为宿主建接口（StderrTail/RestartAsync 面）；其价值在「恢复屏 + 重启 + 导航」三 UI 动作的时序接线，策略核已空（判序逻辑在 ExitOrchestration），端口化收益不抵间接层成本。落败。
- **`CloseBehaviorPreference` 留主工程经端口注入**：唯一消费方托盘路由在 Presentation，偏好读写是纯文件边界、无策略核；照 B2 适配器整族同迁先例落 Infrastructure 最诚实，不造接口。落败。
- **`ExternalLinkCommandRouter` 迁 Infrastructure**：它是 UI 命令面（`ICommandRouter` 实现）且开浏览器动作已经 `Opener` 委托注入、默认 `SystemBrowser`——父 ADR 明定命令路由留 Presentation，D005 白名单条目续守。落败。

## Consequences

- 三层归属定形：主工程 30 文件全部为组合根 + UI 桥（Presentation/组合根），Core 18 文件纯逻辑，Infrastructure 43 文件边界适配器；「新代码先问进哪层」有了完整判据表（留守判据 + 本 ADR 迁移映射）。
- **614/614、0 警告、format 与门禁全绿；零行为变更判据**：4 文件 `git mv`；行为面仅一处等价改写（PreferencesFile 序列化上下文换家，选项/键名逐项比对一致）。
- 过渡态已知项：A2/A4/A5 命名空间门、verify-code-health 单扫主工程、D005 白名单残留条目（ExternalLinkCommandRouter/LegacyHomeNotice/CloseBehaviorPreference 等）均按既定路线 B4 收敛；测试徽章基线随标准跟值批收口（614/614、`4040/7306 = 55.30%`；[覆盖合并口径](../testing/2026-09-14-coverage-baseline-multi-project-merge.md)）。
- CI 工作流零变更；打包 publish 主工程自动携带 Core/Infrastructure DLL。

## Related

- [2026-09-14-official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md)：父 ADR（B1–B4 路线）；[2026-09-14-clean-architecture-b1-core-extraction](2026-09-14-clean-architecture-b1-core-extraction.md)/[2026-09-14-clean-architecture-b2-infrastructure](2026-09-14-clean-architecture-b2-infrastructure.md)：先例（命名空间保持、序列化上下文随类型走均沿袭）。
- [docs/architecture-standards.md](../../../../docs/architecture-standards.md)：规范正文（R1–R5 规则单一事实源）。
