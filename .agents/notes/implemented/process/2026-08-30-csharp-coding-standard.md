# Agent Note: csharp-coding-standard（采用权威 C# 编码规范）

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

审查 `Program.cs`（1337 行，`Main` 单方法 ~1300 行）引发「该按规范拆分」的讨论。初议以**自拟的「方法 ≤30 行 / 文件 ≤1000 行」**为准；用户指出方向错误——应先确立**权威的 C# 编码规范**作基准，而非套用自创阈值。经检索核实：（见下）这三份权威规范**均无**「方法/文件行数上限」这类规则。

## Decision

**采用权威 C# 编码规范为本项目基准，并落地为可执行配置 + 单一事实源文档；不把「方法/文件行数上限」作为编码规范规则。**

- **规范基准**：
  - [dotnet/runtime C# Coding Style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md)（生产级、配 `.editorconfig` + `dotnet format` 可强制）；
  - [Microsoft .NET C# Coding Conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)（命名/格式/语言惯用）；
  - [.NET Framework Design Guidelines](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/)（公共 API/命名/类型设计）。
- **落地**：
  - 仓库根新增 **`.editorconfig`**（基准规则，IDE/Roslyn 自动格式化与提示）；
  - 新增 **`docs/coding-standards.md`**（规范摘要 + 关键约定 + 与 cookbook/architecture/AGENTS 的分层关系 = 单一事实源）；
  - 根 `AGENTS.md`「编码约定」加一行指向该文档 + 字数预算表加 `docs/coding-standards.md ≤ 500 词`；`doc-budgets.manifest.json` 同步；
  - **CI 门禁 + build 强制**：ci.yml build-test 加 `dotnet format --verify-no-changes --severity warn`；规范升级（1–5 收口，提交 `008ac1a`）：风格规则升 `warning`、csproj 加 `AnalysisLevel latest`+`EnforceCodeStyleInBuild`+`EnableNETAnalyzers`+`StyleCop.Analyzers 1.1.118`+`NoWarn AD0001`，`dotnet format` 全量合规（91 .cs、var→显式类型等）——门禁与 `dotnet build` 现在都强制风格/分析器。
- **行数指标不作为规范**：权威规范无此规则；用户拍板——不加行数校验脚本，不把 30/1000 当规范强推（防止"规则无工具兜底、靠人自觉"的假合规）。拆 `Program.cs` 是独立工程问题（见另条），不由此决定。

## Alternatives considered

- **自拟「方法 ≤30 行 / 文件 ≤1000 行」**：被否——非任何权威规范来源；无 Roslyn/editorconfig 原生支持（需自写 analyzer），易导致过度分解伤可读性；且与「可被工具自动强制」的官方风格规则定位背离。
- **仅采用 Microsoft docs 约定（样本导向）**：作为基准一并引用，但不能独当生产规范（该约定为文档/示例导向、不限制语言特性）；以 dotnet/runtime 风格为主。
- **只落 `.editorconfig` 不写文档**：无单一事实源，规范散落在配置里；被否。
- **只写文档不落 `.editorconfig`**：无强制执行/IDE 引导面；被否。
- **只落地 `.editorconfig` + 文档、不配 CI 门禁（用户初选 B）**：后用户改口「加入 CI 门禁」——先按 `.editorconfig` 一键归一既有代码（纯 whitespace），再挂 `dotnet format --verify-no-changes`，新代码不符即 fail loud。此为本 ADR 最终采纳路径（见 Decision）。

## Consequences

- IDE/Roslyn 依 `.editorconfig` 自动格式化与提示新代码；`dotnet format` 可随时对齐。
- 既有代码已**全量合规**（`dotnet format` 自动修 91 .cs：1812 处 var→显式类型、nameof、大括号、using 排序；人工 10 个静态字段改 `s_`；1 处 SA1401）；命名规则经 `required_modifiers` 修正后 100% 合规。
- 后续 `Main` 等重构以本规范为准；CI `dotnet format --verify-no-changes` + `dotnet build`（EnforceCodeStyleInBuild）双重强制——新代码或后续改动不符（含命名违规，format 对 IDE1006 返回 exit 2）会在 CI 红。
- 已知取舍：StyleCop 元素排序四件套（SA1201/02/04/14）为 `suggestion`（工具链无自动重排修复器、存量 ~310 处）；IDE0051/52 为 `none`（反射/DI 误报）——后续专项。
- doc-budgets 新增 `docs/coding-standards.md`（≤500 词），AGENTS.md 字数预算同步。

## Related

- [docs/coding-standards.md](../../../../docs/coding-standards.md)：规范正文（本 ADR 落地的文档）。
- [docs/cookbook.md](../../../../docs/cookbook.md)：踩坑判别，与本规范分层（规范 vs 经验）。
- 仓库根 `.editorconfig`：规则机器化。
