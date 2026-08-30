# Agent Note: architecture-mechanization（架构规范机械化——规则/工具/阈值/接入一次补齐）

Status: proposed

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

新建 [architecture-standards](../../implemented/process/2026-08-30-architecture-standards.md) 的 R1–R5 目前全是「靠评审自觉」的**软规则，无机器强制**；而 coding-standards 靠 `dotnet format --verify-no-changes` / `EnforceCodeStyleInBuild` / 分析器做到 build-红级强制。对比之下架构规则不执行即退化，正是「屎山」的**结构性根因**：不加约束，组合根继续膨胀（`DesktopBootstrap` 873 行）、依赖方向失控（意大利面）、边界细节直漏进业务层。须把**可机器化的架构规则全量**接入 build/CI 门禁，让机器兜底而非靠人自觉。

## Proposal

**「全部补齐、一次性解决」——建立一套完整机制，把架构规范 R1–R5 中可机器化的规则全部变门禁，覆盖面一次到位；现有超出阈值的存量一次清账，避免新门禁一挂即红。** 分四通道，各配阈值/规则、接入门禁。

### 通道一 · 尺寸健康闸（R4 反上帝对象 + R1 组合根尺寸）

- **机制**：`scripts/verify-code-health.py`（对齐 verify-* 脚本模式，`--self-test`），扫描 `src/**`（排除 `obj/`、`bin/`、生成物）。
- **规则(阈值可配置，默认)**：
  - `F1` 单文件超 **400 行**。
  - `F2` 单方法（`MethodDeclaration` / 本地函数）超 **80 行**。
  - `F3` 组合根文件（`DesktopBootstrap*.cs` + `Program.cs`）超 **400 行**（更严，防组合根继续膨胀）。
  - `F4` 组合根方法超 **60 行**。
- **边界**：`// verify-code-health: ignore` 行内豁免（有例外时显式声明理由，防静默放宽）；脚本按「行数 / 方法行数」粗扫 + 简单 `{` 配对统计（非 Roslyn，零依赖、够判 F1/F3；F2/F4 方法行数用逐方法 brace 计数，近似）。
- **接入**：pre-commit（快）+ pre-push（增量）+ ci 独立 `code-health` job（或并入 build-test，按 `scripts/` 变更判定）。

### 通道二 · 架构测试（R2 依赖方向/禁循环 + R3 边界代理）

- **机制**：引入 `NetArchTest.Rules`（轻量、断言式），写成 xunit 测试放 `tests/`，随 `dotnet test` → CI 门禁。
- **规则**：
  - `A1` **层边界**：子域命名空间互不引用（`Services.Update` ↔ `Services.Tray` ↔ `Services` 之间按既定依赖方向断言；外层不得反向依赖内层）。
  - `A2` **禁循环**：跨命名空间无循环依赖（NetArchTest 循环检测）。
  - `A3` **边界实现不泄漏**：基础设施/适配器实现类型（`*Process`/`*Downloader`/`*Client`/注册表/文件系统类）不得被**非组合根**类型直接实例化/引用（命名空间 + 类型名模式）。
  - `A4` **组合根不被内层依赖**：`Services/` 等不得反向引用组合根命名空间。
- **阐明**：本项目是单项目 + 逻辑分层，NetArchTest 在**命名空间/类型层级**断言，完全适用单项目布局。

### 通道三 · 自定义 Roslyn 分析器（R5 跨切面契约 + R3 边界）

- **机制**：新增分析器工程 `src/DeepSeek.Harness.Desktop.Analyzers`（`DiagnosticAnalyzer`，打包为 analyzer），主/测试 csproj 引用，诊断设 `warning`（build 0 警告即拦）或关键 `error`。
- **规则（DIAG 序号，各带 `severity` + `helpLink`）**：
  - `D001` **async 方法缺 `Async` 后缀**——`.editorconfig` 命名功能做不了完整 async 尾缀（roslyn#40050），必须分析器。
  - `D002` **禁 `async void`**——非事件处理器返回 `void` 的 `async` 方法警告（防未观察异常）。
  - `D003` **空 catch 体未命名**——`catch` 后空体 / 只注释无动作：要求命名所吞异常或在 catch 内 `_log`/`throw`。
  - `D004` **日志未走 HostLog**——`System.Console.WriteLine`/`Console.Write` 出现在**非许可类**（白名单：`HostLog`、`Program`/组合根诊断、dev 工具）→ 警告（对外部边界类例外可配）。
  - `D005` **禁非边界层直调外部基础设施**——`Process`（`System.Diagnostics.Process`）、`HttpClient`/`HttpRequestMessage`、`File`/`Directory`/`FileStream`/`Path` 等出现在**非边界类**（白名单：`Services/HarnessRuntimeHost`、`*Downloader`、`Update/`、`InstallHelper`、`CliShim*`、`SystemBrowser` 等）→ 警告。这是 R3 边界抽象的「廉价代理」。
- **允许清单**：白名单（边界类 / 例外）置于分析器源内常量或约定标记（如 `[Boundary]` attribute），避免每类手列。

### 通道四 · 存量一次清账

- 新门禁一挂即红（当前 `DesktopBootstrap` 873 / `RuntimeBootstrap` 743 / `HarnessRuntimeHost` 534 等均超 400、A3/D005 存量违规众多）。故设**前置清账**：
  - 按 [architecture-standards](../../implemented/process/2026-08-30-architecture-standards.md) 把 `DesktopBootstrap`/`DesktopBootstrap.Startup` 拆到阈值内（组合根去业务、组件化进 `Services/`）——本身是既定重构（P0 后置专项）。
  - 其余超阈值文件分层拆分到 ≤400/方法 ≤80；A3/D005 存量违规一次归位。
  - 清完才启用严格阈值（或先宽后紧：初始阈值放宽到「现状 + 10%」，再逐步收紧到 400/80）。
- **「一次性」语义**：规则全集、工具、阈值、门禁一起定稿落地；存量的拆也纳入本次（而非留到未来）。

### 通道五 · 工作流集成（新增/修复分流 + 先拆再上硬门槛）

- **feature-flow 分流**：把 `.agents/workflows/feature-flow.md` 拆为两条明确路径——
  - **新增**（高严格度）：ADR 必写（非平凡）→ 架构适配检查（新组件进 `Services/`、组合根不增逻辑、边界经接口）→ 机械化验证 → 单元 + 行为级回归/快照 → 三重审核（feature/跨模块/安全面/IPC/发版）。
  - **修复**（根因导向、较轻但必经验证）：fail loud 取证 → 复现 → 根因修 → 回归/钉子测试；触碰安全面/IPC/发版/跨模块或非平凡类则三重审核；**不补丁式**（治标不治本不算修复）。
- **先拆再上 = 硬门槛**：`verify-code-health`（pre-commit/pre-push/CI）以 fail loud 强制——(**a**) 任何 diff 不得把文件/方法**顶过阈值**（F1/F2/F3/F4）；(**b**) 变更触碰**已超阈值**的文件（如 `DesktopBootstrap`）时，须**先把该文件拆到阈值内**（拆分是合并前置、本次变更应让文件缩小而非增长）。超阈值的实际改动直接门禁红，不许并入。
- **机械化先实施、再引用**：**实施顺序** = 先落地通道一~四（让机械化门禁真实存在）→ 再改 `feature-flow.md` 把机械化门禁设为**强制验证步骤**（门禁含 verify-code-health / NetArchTest / 分析器），并写入「done = 通过全部验证」。
- **完成定义**：一笔变更算完成 = ADR（需要时）→ 机械化门禁（尺寸/依赖/分析器）→ 测试（含回归）→ 评审（需要时）→ CI 全绿。
- **债务规则**：不新增未登记债务——`TODO(...)` 带理由 + 计划归还；不改范围外代码（越界记 TODO）。

### 接入门禁汇总

| 通道 | 产物 | 接入 |
|---|---|---|
| 一 尺寸健康闸 | `verify-code-health.py` | pre-commit + pre-push + CI `code-health` job（先拆再上硬门槛） |
| 二 架构测试 | `tests/ArchitectureTests.cs`（NetArchTest） | `dotnet test` → CI build-test |
| 三 分析器 | `DeepSeek.Harness.Desktop.Analyzers` | csproj 引用 → `dotnet build`（0 警告门禁）+ CI |
| 四 存量清账 | 拆分/归位 | 与一二三相结的前置 commit |
| 五 工作流集成 | `feature-flow.md` 新增/修复分流 | 机械化落地后设为强制验证步骤；`done`=通过全部验证 |

### 不能机器化的（留评审）

R1「组合根只装配」**语义**、R3 完整「边界抽象完备度」、R5 部分异常策略（只捕获能处理）/CancellationToken 贯通完整性——需语义理解，硬做误报高、过度设计。以人工评审 + 三重审核兜底；其**关键退化面已用廉价代理覆盖**（F3/F4 组合根尺寸、A3/D005 边界直调）。

## Alternatives considered

- **只做性价比最高的两样（尺寸闸 + 架构测试）**：见效快、成本低；但留大量软规则（契约/边界/异常），且用户明确要求「全部补齐、一次性解决」。落败；做全集。
- **用 ArchUnitNET 而非 NetArchTest**：ArchUnitNET 更强（类/成员级依赖、更全），但更重、学习成本高；本需求（命名空间层边界 + 禁循环 + 类型模式）NetArchTest 更轻、够用。采 NetArchTest，后续不够再升级。
- **尺寸健康闸用自定义 Roslyn 分析器而非脚本**：分析器精确（方法级）、build 一体；但需建分析器工程、门槛高。用**脚本**做尺寸闸（零依赖、够判 F1/F3，F2/F4 近似 brace 计数）贴合 verify-* 生态；精确的契约类仍走分析器（通道三）。
- **async 尾缀靠 .editorconfig 命名规则**：Roslyn 命名功能是子集、做不了完整 async 尾缀（roslyn#40050 明确）；落败——置通道三自定义分析器。
- **R1「只装配」做语义门禁**：检测「是不是业务逻辑」需语义分析，误报/过度设计；落败——用组合根尺寸（F3/F4）+ 依赖方向（A4）代理，语义交评审。
- **存量不拆、新门禁直接放暖通阈值**：阈值放暖到能容纳现状则失去约束价值；落败——存量一次拆清，阈值定真值，宁可先做清账再上闸。
- **先改 feature-flow 再实施机械化**：feature-flow 会引用尚未存在的门禁，成「空承诺」；落败——先落地机械化门禁（通道一~四）、feature-flow 再引用真门禁（通道五）。
- **「先拆再上」仅作软提示而非硬门槛**：软提示会被绕过、阻止不了「在超阈值文件里继续加」，恰是「屎山」温床；落败——设硬门槛（diff 顶过阈值即红、触碰超阈值文件须先拆）。

## Consequences

- 架构规范从「软约束」变 **build/CI 门禁**：违规即红——治「屎山」的结构性根因。R2 杀意大利面、R4 刹住上帝对象、R5 补齐跨切面契约、R3 用代理防边界直漏。
- 新增：`verify-code-health.py` + `DeepSeek.Harness.Desktop.Analyzers` 工程 + `NetArchTest.Rules` 依赖（测试）+ `tests/ArchitectureTests.cs`；门禁矩阵扩展（code-health job、架构测试、分析器 build 门禁）。
- **工作流（feature-flow）从「类型无关」变「新增/修复分流」**：新增高严格度、修复根因导向，均必经机械化验证；「先拆再上」为硬门槛——超阈值的 diff 不许并入，触碰超阈值文件须先拆，从流程上堵死「加功能就烂、打补丁继续烂」。
- 存量：`DesktopBootstrap`/`RuntimeBootstrap`/`HarnessRuntimeHost` 等超大文件按架构规范拆分；A3/D005 等存量违规归位——一次清账，随本方案实施。
- 文档同步：coding-standards/architecture-standards 的「强制力度」、AGENTS 质量门清单（加 `verify-code-health.py`）、README 测试徽章（架构测试计入总数）、feature-flow 分流。
- **本 ADR 为 proposed（只定方案，不实施）**：拍板后按 feature-flow 实施（通道一/二 → 三 → 四清账 → ⑤ feature-flow 分流引用，每步三重审核；`dotnet test`、门禁、CI 全绿）。

## Related

- [2026-08-30-architecture-standards](../../implemented/process/2026-08-30-architecture-standards.md)（implemented）：本 ADR 对其 R1–R5 做机械化覆盖。
- [2026-08-30-csharp-coding-standard](../../implemented/process/2026-08-30-csharp-coding-standard.md)（implemented）：强制力度参照（format/analyzer/build 0 警告）。
- 仓库根 `.editorconfig`、`scripts/verify-*.py`、`.github/workflows/ci.yml`：本方案接入的门禁/脚本生态。
