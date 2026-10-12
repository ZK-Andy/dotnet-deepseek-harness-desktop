# Agent Note: D003 自研 Roslyn 分析器 DSH0001

Status: implemented

Review: FULL/2026-10-12/R1=ok R2=ok R3=ok

## Problem

D003（空 `catch` 必须命名所吞异常）是最后一块无机器执行的契约：`verify-code-conventions.py` 明言 D001–D003 留评审（"needs Roslyn semantics"），根 AGENTS.md 评审清单写"D003 暂无现成 analyzer，留兜底（自定义 analyzer 试点另立项）"。现状实测：全树 242 个 `catch` 中恰 2 处违反——`MarketInstallHelper.Json.cs:212`（注释空体）与 `CliShimTests.cs:257`（真空体），皆为无类型 `catch`。规则精确语义以 coding-standards 示例为准：`catch (InvalidOperationException) { /* … */ }`（具名类型、无变量）即合规——禁的是无类型 `catch`，不是无变量 `catch`。`catch {}` 与 `catch (Exception)` 语义等价（现代 .NET 皆吞全部异常），合规化零行为变更。

## Decision

自研分析器 `tools/DeepSeek.Harness.Desktop.Analyzers`（netstandard2.0 + `LangVersion latest` + `EnforceExtendedAnalyzerRules`；`Microsoft.CodeAnalysis.CSharp` 钉 4.13.0 进 CPM——引用版本 ≤ 编译器版本即前向兼容，本地缓存有该版、离线可还原）：规则 DSH0001，空体 = 零语句（注释不计，只有 Roslyn 语义能判定，证实脚本路线不可行）、无类型 `catch` 即 `DiagnosticSeverity.Error`，生成代码跳过（`ConfigureGeneratedCodeAnalysis(None)`，Ryn 生成代码不受扰）。`dotnet_diagnostic.DSH0001.severity = error` 进根 `.editorconfig`（VSTHRD100/200 先例）。

接线 7 工程（src 3 + tests 3 + PayloadSmoke）一律 `ProjectReference` + `OutputItemType="Analyzer"` + `ReferenceOutputAssembly="false"`（Ryn.Ipc.Generator 先例）：编译期工具引用，不产生程序集引用，R2 依赖方向不变。`ArchitectureTests.ProjectReferences` 取数口径随之收窄为"只计程序集引用"（过滤 `OutputItemType="Analyzer"` 节点）——R2 的真执行仍是程序集级互证（`CoreAssembly_HasZeroOuterReferences` 等未动，仍过）；Core csproj 的"零引用"注释同步澄清为程序集引用。

存量 2 处合规化：`catch` → `catch (Exception)`（注释保留），零行为变更。单测 `tests/DeepSeek.Harness.Desktop.Analyzers.Tests` 6 例（in-memory 编译断言，TRUSTED_PLATFORM_ASSEMBLIES 取框架引用，零新增测试 SDK 包）：无类型真空体/注释空体/filter 空体各 1 红（含 error 级断言）+ 无类型非空/具名真空/具名注释空 3 放行。AGENTS.md 评审清单 D003 移出兜底（清单只减不增）、coding-standards 首语与 `verify-code-conventions.py` docstring 同步。基线 → 1042/1042（+6 本批；另含 `99a8d15` 加测未同步的 +1 存量漂移，一并对账）。

## Alternatives considered

- **脚本启发式扩 `verify-code-conventions.py`**：空体判定需区分语句与注释（`{ /* … */ }` 合规 vs `{ }` 违规全在 Roslyn 语义内），正则只能近似；且用户已选直接 Roslyn。落败。
- **现成 CA1031（catch 通用异常）**：全树有意 `catch (Exception)` 几十处（兜底/降级语义），CA1031 全红等于逼全树改写异常策略，药量远超病灶。落败。
- **severity 先 warning 过渡**：CI 为普通 `dotnet build`（不拦 warning），warning 即"建议"，违反"在做决定的操作里强制"；一步到位 error。落败。
- **roslyn-analyzers 式发布跟踪（AnalyzerReleases.*.md）**：RS2007 拒收标准头（实查 3.11.0 资源仍报头无效），单规则分析器的跟踪文件属仪式成本；发布史 = 本 ADR + git tag，RS2007/RS2008 在分析器工程内窄压制。落败（暂）。
- **分析器打 NuGet 包分发**：包版本/源/还原链全套成本；仓内 ProjectReference + Analyzer 元数据已有 Ryn 先例且随源码走。落败。

## Consequences

- D003 从评审兜底移入 build 门禁：后续无类型空 `catch` 在编译期即红（含 CI）；AGENTS.md「评审检查项」D003 条同步失效化（D001/D002 先例措辞）。
- R2"零引用"口径永久澄清为程序集引用：后续工具类 Analyzer 引用走同款元数据，不再触 R2；`ArchitectureTests` 的 csproj 面断言只看程序集引用，方向证明以后者 + 程序集级互证为准。
- 新分析器规则的家 = 本工程：后续契约机器化（如 D004/D005 的 Roslyn 版）直接加规则 + 单测，不另起工程。
- 已知局限：只认"空体 + 无类型"交集；`catch { Foo(); }`（无类型非空）与 `catch (Exception) { }`（具名真空）按规则字面放行——皆为有意从宽（前者非空、后者已命名），非盲区。

## Testing

- `dotnet build` 全解 0 警告（NU1900 离线噪音除外）0 错误；DSH0001 上线即红 2 处存量（`MarketInstallHelper.Json.cs:212`、`CliShimTests` 因依赖编译失败当时未及分析，修复 Infra 后复核逻辑覆盖），合规化后全绿。
- `dotnet test` 1042/1042（Core.Tests 209 + Tests 224 + Infrastructure.Tests 603 + Analyzers.Tests 6；基线文件与 README 双语徽章同步）。
- 机器门禁：adr-format / cookbook / doc-budgets（cookbook 2793/2804，主档内压缩腾挪、无提额）/ md-links / readme-badges / handoff-structure / code-health / compose-root / registration-discipline / code-conventions 全绿；review-tier FULL（门禁判据面）待三审收口后持 Review 行过关。

## Related

- 前序：[coupling-shift-left-gates](2026-10-01-coupling-shift-left-gates.md)（D003 另立项出处）与 [composition-mechanism-container-assembly](../../proposed/architecture/2026-10-08-composition-mechanism-container-assembly.md)（R1 闸同源）。
- 规范：[architecture-standards.md](../../../../docs/architecture-standards.md) R2；[coding-standards.md](../../../../docs/coding-standards.md) 行为契约；方法论：[review-tier-escape-proofing](../process/2026-09-03-review-tier-escape-proofing.md)（FULL 证据行）。
