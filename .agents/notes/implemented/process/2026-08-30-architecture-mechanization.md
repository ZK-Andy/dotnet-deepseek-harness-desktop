# Agent Note: architecture-mechanization（架构规范机械化——规则/工具/阈值/接入一次补齐）

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

新建 [architecture-standards](../../implemented/process/2026-08-30-architecture-standards.md) 的 R1–R5 目前全是「靠评审自觉」的**软规则，无机器强制**；而 coding-standards 靠 `dotnet format --verify-no-changes` / `EnforceCodeStyleInBuild` 做到 build-红级强制。对比之下架构规则不执行即退化，正是「屎山」的**结构性根因**：组合根继续膨胀（`DesktopBootstrap` 873 行）、依赖方向失控（意大利面）、边界细节直漏进业务层。须把**可机器化的架构规则全量**接入 build/CI 门禁，让机器兜底。

二次盘点发现两处**归属/设计偏差**需先修，本 ADR 一并定案：
1. **归属错位 + 非单家**：`architecture-standards.R5` 把 async/取消/异常处理/日志（**代码级约定**）放进架构规范；且这几项同时出现在根 `AGENTS.md` 编码约定，违反「每个事实只有一个家」。
2. **执行序鸡生蛋**：先上硬门槛与「先清账」互相依赖（任一 diff 触碰超阈值文件即红）。

## Decision

**「全部补齐、一次性解决」**，按下列五通道 + 修订执行序落地。规则全集、工具、阈值、门禁一次定稿；存量（超阈值文件/历史违规）一次清账。

### 通道〇 · 标准重构（先修归属与单家，纯文档）

- **行为契约迁回编码规范**：把 `architecture-standards.R5` 的 async/取消、异常处理、日志约定**迁入 `coding-standards.md`**（它们是「单段代码怎么写」）；`coding-standards.md` 预算 **500→1000**（提额，PR 说明理由）。
- **架构规范收紧为结构+契约**：`architecture-standards` 保留 R1 组合根纪律 / R2 依赖方向 / R3 边界抽象（含 IPC 帧契约）/ R4 反上帝对象健康闸；**移除 R5**（行为契约归编码规范）。
- **去重**：编码规范（async/异常/日志）+ AGENTS 编码约定各条只保留一个家（细则进 coding-standards，根 AGENTS 留一行链接/规则指针）。

### 通道一 · 尺寸健康闸（R4 + R1 组合根尺寸）

- `scripts/verify-code-health.py`（对齐 verify-*，`--self-test`），扫 `src/**`（排除 `obj/`、`bin/`、生成物），阈值可配：
  - `F1` 单文件超 **400 行**；`F2` 单方法超 **80 行**；`F3` 组合根文件（`DesktopBootstrap*.cs`+`Program.cs`）超 **400 行**；`F4` 组合根方法超 **60 行**。
- `// verify-code-health: ignore` 行内豁免（显式理由，防静默放宽）。

### 通道二 · 架构测试（R2 依赖方向/禁循环 + A4 组合根 + 新 gate A5；A1/A3 留评审）

- 引入 `NetArchTest.Rules`，写成 `tests/ArchitectureTests.cs`，随 `dotnet test` → CI。
- `A1` 层边界（`Services.Update`/`Services.Tray`/`Services` 按既定依赖方向；外层不反向依赖内层）；`A2` 禁命名空间循环依赖；`A3` 应用层不得直引具体基础设施实现（`*Process`/`*Downloader`/`*Client`/注册表/文件系统类不得被非组合根直接实例化/引用），即 R3 边界抽象；`A4` 组合根不被内层依赖。——`A1`/`A3` 经用户拍板**保留为评审项、不作硬门禁**（见 Consequences A-规则校准：A1 对薄壳过度严格、A3 需接口抽取），`A2`/`A4` 为真实可强制。
- **新 gate（③）**：`A5` **新类型必须进 `Services/` 或子域**（禁落在根命名空间/`DesktopBootstrap` 组合根——治「新功能塞组合根」）。`A6` **IPC 跨界 ID 用强类型**（禁跨包/事件帧裸 `string` ID，治「裸 string 跨包」）经用户拍板**保留为评审项、不作硬门禁**（见 Consequences A-规则校准），不机器化。

### 通道三 · 契约扫描脚本（**不建分析器工程**；D001–D003 留评审）

- `scripts/verify-code-conventions.py`（grep 词法，`--self-test`，配合白名单）：
  - `D004` **日志未走 HostLog**——`System.Console.WriteLine`/`Console.Write` 出现在非许可类（白名单：`HostLog`、组合根诊断、dev 工具）。
  - `D005` **禁非边界层直调外部基础设施**——`Process`/`HttpClient`/`File`/`Directory`/`FileStream`/`Path` 出现在非边界类（白名单：`Services/HarnessRuntimeHost`、`*Downloader`、`Update/`、`InstallHelper`、`CliShim*`、`SystemBrowser` 等）。
- **不机器化（留评审 + 三重审核）**：`D001` async 缺 `Async` 后缀 / `D002` 禁 `async void` / `D003` 空 catch 体未命名——需 Roslyn 语义、为建分析器工程投入产出比低；交代码评审/三重审核（对齐 R1/R5 语义面）。

### 通道四 · 存量一次清账

- 新门禁一挂即红（`DesktopBootstrap` 873 / `RuntimeBootstrap` 743 / `HarnessRuntimeHost` 534 等超 400；`A5/D004/D005` 存量违规众多）。前置清账：
  - 按 [architecture-standards](../../implemented/process/2026-08-30-architecture-standards.md) 把 `DesktopBootstrap`/`DesktopBootstrap.Startup` 拆到阈值内（组合根去业务、组件化进 `Services/`）。
  - 其余超阈值文件分层拆分到 ≤400/方法 ≤80；`A5/D004/D005` 存量违规一次归位；异步/异常/日志按迁徙后编码规范刷一遍（A3/A6 为评审项，不属本通道机械清账面）。
- 清完才启用严格阈值（或先宽后紧：初始放宽到「现状+10%」再收紧）。

### 通道五 · 工作流集成（新增/修复分流 + 先拆再上硬门槛）

- `feature-flow.md` 拆两条：**新增**（ADR 必写 → 架构适配检查 → 机械化验证 → 单元+行为级回归 → 三重审核）；**修复**（fail loud 取证 → 复现→根因→回归/钉子；触安全面/IPC/发版/跨模块或非平凡类则三重审核；不补丁式）。
- **先拆再上 = 硬门槛**：`verify-code-health` fail loud 强制——(a) diff 不得把文件/方法**顶过阈值**（F1–F4）;(b) 触碰已超阈值文件须**先拆到阈值内**（拆分是合并前置、本次变更应让文件缩小）。
- **done = 通过全部验证**：ADR（需要时）→ 机械化门禁（尺寸/依赖/契约）→ 测试（含回归）→ 评审（需要时）→ CI 全绿。
- **AI 兜底评审（补 upstream 技能缺口）**：上游 dsh-code-review 为 DSH 仓库通用清单、不含本项目规则；故「留评审」的 D001–D003 与 R1/R3/IPC 语义，须由评审代理**显式补审**——在根 `AGENTS.md`「评审检查项（AI 兜底）」+ feature-flow 步骤 5 引用中列出，评审代理按 coding-standards/architecture-standards 核对（详见工艺落地时更新 [feature-flow](../../../workflows/feature-flow.md) 步骤 5）。
- **债务规则**：不新增未登记债务（`TODO(...)` 带理由+计划归还）；不改范围外代码。

### 执行顺序（修订，解鸡生蛋）

1. **门禁 warn/report 模式**——F/A/D004/D005 先建成「只报告不 fail」，跑一遍**枚举全部存量违规**（即审计）。
2. **存量清账**——按报告拆 god files、归位违规；**含通道〇 标准重构**（行为契约迁编码规范）。体量最大、风险最高，走三重审核。
3. **门禁转 fail**——代码干净后收紧，先拆再上硬门槛生效。
4. **feature-flow 分流 + 引用强制验证**——写「done=通过验证」。

### 接入门禁汇总

| 通道 | 产物 | 接入 | 变更 |
|---|---|---|---|
| 〇 标准重构 | coding-standards(提额)/architecture-standards | 文档 + 预算 + AGENTS | ① |
| 一 尺寸健康闸 | `verify-code-health.py` | pre-commit + pre-push + CI `code-health` job（先拆再上） | — |
| 二 架构测试 | `tests/ArchitectureTests.cs`（NetArchTest） | `dotnet test` → CI | +A5（A6 留评审，不作门禁） |
| 三 契约扫描 | `verify-code-conventions.py` | pre-commit + pre-push + CI | **替代分析器工程**；D001–003 留评审 |
| 四 存量清账 | 拆分/归位 | 与一二三相结的前置 commit | ②执行序 |
| 五 工作流集成 | `feature-flow.md` 分流 | 机械化落地后设为强制验证步骤 | ② |

## Alternatives considered

- **只做性价比最高的两样（尺寸闸 + 架构测试）**：留大量软规则；用户明确要「全部补齐、一次性解决」。落败；做全集。
- **用 ArchUnitNET 而非 NetArchTest**：更强但更重、学习成本高；本需求（命名空间层边界+禁循环+类型模式+两条 gate）NetArchTest 够、轻。采 NetArchTest。
- **尺寸闸用自定义 Roslyn 分析器而非脚本**：精确但需建工程、门槛高；用脚本贴合 verify-* 生态、够判 F1–F4。采脚本。
- **async 尾缀靠 .editorconfig 命名规则**：Roslyn 命名功能是子集、做不了完整 async 尾缀（roslyn#40050）；落败——迁回编码规范靠评审/IDE 兜底，不机器化。
- **建独立 Analyzer 工程跑全 5 条（D001–D005）**：精确、build 一体；但为一个 8.8k 行桌面壳建独立分析器工程过重，D004/D005 用 grep 就够、D001–D003 投入产出比低。**落败（二次分析定案）**——撤销独立分析器工程，改混合③：F/A/D004/D005 机器化，D001–D003 留评审。
- **R1「只装配」做语义门禁**：需语义分析、误报高、过度设计；落败——用组合根尺寸（F3/F4）+ 依赖方向（A4）+ 新类型进 Services（A5）代理，语义交评审。
- **行为契约留在架构规范 R5（方案 A）而非迁回编码规范**：归属错位（async/异常/日志是代码级约定）、且与根 AGENTS 编码约定重复（非单家）；落败——迁回编码规范、架构规范收紧为结构+契约（通道〇）。
- **先改 feature-flow 再机械化**：引用未存在的门禁成空承诺；落败——先落地门禁、feature-flow 再引用。
- **「先拆再上」仅软提示**：会被绕过、正是屎山温床；落败——设硬门槛。
- **存量不拆、新门禁放宽阈值**：阈值放暖失去约束；落败——存量拆清、阈值定真值。

## Consequences

- 架构规范从「软约束」变 **build/CI 门禁**：R4/F 刹住上帝对象、D004/D005 补契约/边界、A5 治「新功能塞组合根」。
- **标准更自洽**：行为契约（async/异常/日志）归编码规范，架构规范只留结构+契约；「一个事实一个家」落地。
- 新增：`verify-code-health.py`（F1-F4）+ `verify-code-conventions.py`（D004/D005）+ `NetArchTest.Rules`（`tests/ArchitectureTests.cs`，A2/A4/A5）；门禁矩阵扩展（CI + pre-commit，`--enforce` 硬门槛）。**不建分析器工程**。
- 存量清账（一次完成）：`Startup`/`MarketInstallHelper`/`HarnessRuntimeHost`/`RuntimeBootstrap`/`DesktopBootstrap` 拆为 <400 的 partial、F2/F4 方法分解到阈值内；D004/D005、A5 归位。
- **A-规则校准（用户拍板保留为评审项）**：原 A1/A2/A3 对薄桌面壳过度严格（要求 ports-and-adapters 重写：应用层不得直引具体基础设施、子域互不依赖），与 architecture-standards「采纳原理，不照抄模板」矛盾。落地校准为真实可强制：A4 组合根不被内层依赖 / A5 新类型必进 Services / A2 子域无真循环（Tray→Update 单向合理耦合）。**A3（应用层不得直引具体基础设施实现，R3 边界抽象）与 A6（IPC 跨界 ID 强类型）保留为评审项、不作硬门禁**（前者需接口抽取、后者未达量产）——留评审/AI 兜底。D001–D003 留评审。
- 文档同步：coding-standards（提额 500→1000）与 architecture-standards 重构、AGENTS 质量门清单（加两个 verify）、README 测试徽章 464→467、feature-flow 分流。
- **落地结果**：`verify-code-health`/`verify-code-conventions` 全清零、`dotnet test` 467/467 全绿、五 verify 门禁全绿；机制在「report → 清账 → fail 硬门槛 → 工作流集成」执行序完成。
- **留评审项补审确认（2026-08-30，对本 ADR 实施链 `641525e^..cf4306f` 三重审核 R1/R2/R3 串行）**：R1 组合根只装配 / R2 D001–D003+IPC 强类型 **无代码 Blocker**（留评审项代码达标：IPC 帧全走 `AppJsonContext` 源生成、无 async void、空 catch 均注释命名所吞）；R3 文档一致性 **1 Blocker**（A6 门禁/留评审口径在本文档内部互斥、与 shipped 现实矛盾——本文档已修）+ 4 Suggestion（已修）。后续评审仍按 AGENTS 评审检查项持续核对 D001–D003/R1/R3/IPC，不作一次性勾销。

## 三重审核执行契约（落地）

三重审核曾被批量背景子代理卡住（无限期不收敛）。落地契约见 [feature-flow](../../../workflows/feature-flow.md) 步骤 5，要点：

- **范围 = 单元 diff**：一次只审一个逻辑单元（提交/相干子集），用 `git diff <base>..<head>` 界定；禁止整仓/全文件快照 + 手工 diff。
- **父级预消化简报**：主会话先给每个代理紧凑清单（文件/模式/风险点/定向检查项），非"加载技能→审一切"。
- **串行 + 有界**：R1→R2→R3 依次，前一个收口才放下一个；每代理设轮次/工具调用上限，到限未收口即中断并返回部分结论。
- **确定性报告**：`Blocker[]`/`Suggestion[]`（文件:行 + 证据）；主会话逐条裁定，一次收口为 `refactor(review)`。
- **行为保持第一证据 = 测试 + 门禁**：评审只补测试/门禁盖不住的语义/文档面，不重复全量重验。
- **纯结构重构走轻审/简化路径**；大批量回溯用 `workflow` 阶段化 fan-out。

## Related

- [2026-08-30-architecture-standards](../../implemented/process/2026-08-30-architecture-standards.md)（implemented）：本 ADR 对其 R1–R4/R3 做机械化；R5 行为契约迁出（见通道〇）。
- [2026-08-30-csharp-coding-standard](../../implemented/process/2026-08-30-csharp-coding-standard.md)（implemented）：承接迁回的行为契约，预算提额。
- 仓库根 `.editorconfig`、`scripts/verify-*.py`、`.github/workflows/ci.yml`：本方案接入的门禁/脚本生态。
