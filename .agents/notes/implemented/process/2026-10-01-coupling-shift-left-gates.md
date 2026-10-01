# Agent Note: 耦合左移——注册点扫描与增量闸

Status: implemented

Review: FULL/2026-10-01/R1=ok R2=ok R3=ok

各路：R1 首轮 0 Blocker（5 Suggestion：3 采纳——R1q 钉死测/ADR 收窄/doc 去括号，1 部分采纳——notes 补行号但不降噪，1 挂账——正则收进 gate_common defer 下次动 gate 时）；R2 首轮 0 Blocker（4 Suggestion 全采纳：ADR 口径修正/HostLog 补行/泛型正则扩展/R1q 夹具/global-using 覆盖）；R3 首轮 0 Blocker（2 Suggestion 转 implemented 时顺手做：Related 反向链/实查范围补一句）。三路结论均在本轮内一次完整返回，无中断。

## Problem

AI 写代码的耦合反复出现：多次重构，每次都是巨大的时间与 token 成本【据用户陈述，未计量】。现有防线覆盖了耦合的一部分，但漏了执行最关键的一段：

- R2（Core 零引用，编译器强制）+ `ArchitectureTests` 守住了**项目边界**；D005（`verify-code-conventions.py`）守住了 BCL 级外部直调（`Process`/`HttpClient`/`File` 等）；`verify-compose-root.py` C1–C4 守住了**根里放什么**。
- 但 R1 的核心句——"具体基础设施类型只允许出现在组合根的 DI 注册处"——今天**没有机器执行**：Presentation 工程合法引用 Infrastructure（编译器放行），根之外的 Presentation 文件直接 `new` 基础设施具体类型，默默通过编译、测试与现有门禁，只能靠评审兜底。
- 层内引用（Presentation/Infrastructure 内部跨域）与单 PR 增量（一次涨几百行）同样零机器覆盖；R4 "不设行数上限"，AI 可合法顶格写。

上游 DSH（`deepseek-ai/deepseek-harness`，2026-10-01 实查）没有现成的防耦合产品：它的解法是插件化结构 + `packages/AGENTS.md` 作者规则 + 机器门禁（`check-workspace-constraints`、`benchmark-next-package-dependency`）+ 评审。关键规则与本案直接相关：**"在做决定的操作里强制"——能被绕过的 facade/wrapper 层面的检查不算执行**。按此标准，R1 注册点纪律今天处于"未执行"状态。

## Decision

按上游验证过的顺序打三闸，本批只落第①闸，后两闸排期不超前：

1. **① 注册点扫描（本批已落）**：`scripts/verify-registration-discipline.py` 在册。Presentation 工程内直接构造或静态使用 `DeepSeek.Harness.Desktop.Infrastructure.*` 命名空间具体类型（显式/目标类型 `new`、静态成员访问；DI 形参/字段声明放行——那是注入不是耦合），仅允许出现在注册文件——`*Registration.cs` 按域扩展 + `DesktopBootstrap.App.cs` 的 `RegisterServices` + 根集合（`Program.cs`/`DesktopBootstrap*.cs`，与 `verify-compose-root.py` 同口径，根内构造仍归 C3 计，双闸永不双计）。`HostLog.*` 豁免：D004 指定的 ambient 日志设施，跨层使用是设计不是耦合。行级启发式 + `gate_common.CSharpLineScanner`。冻结帽 R1a=1（`new StartupNoticeService`，迁移出本批范围，帽将其冻结）、R1b=15（ shipped 静态站点），零余量：新增即红。接线 pre-commit + ci.yml 独立 step（与组合根闸同位）；`--self-test` 十夹具；默认 report，`--enforce` 才红。
2. **② PR 增量闸（下批）**：依赖扇出 diff + 单文件单 PR 净增上限（对标上游 `benchmark-next-package-dependency`）。
3. **③ D003 自定义 analyzer（另立项）**：空 catch 命名，Roslyn analyzer；本批不动。

## Alternatives considered

- **买现成工具治耦合（codebase-memory-mcp / GitNexus / aoci-code）**：三者经 2026-10-01 实查均为"治检索"（导航/记忆/评审透镜），耦合是决策问题不是检索问题，药不对症；另有 license（GitNexus 非商业）与 schema 税（17 工具 vs 现状 5）成本。落败。
- **上游 weighted-approval 式重评审**：多人仓的重流程，单人 + agent 成本扛不住；本仓三重审核代理已是轻量对版。落败。
- **Roslyn analyzer 一步到位**：工程化成本高（D003 立项在先）；行启发式 + 封闭清单已覆盖本批回归形态（注册外 `new`），误报由清单显式更新消化（`compose-root-gate` ADR 同款取舍）。落败（暂）。
- **只做②增量闸不做①**：总量闸不挡"放错地方"——把耦合藏进增量预算内仍可行，正是"只改 F3 不加语义闸"先例中已落败的形态。落败。

## Consequences

- R1 注册点纪律首次有机器执行：注册外新增直接构造或静态调用在 pre-commit 即红。上游"在做决定的操作里强制"标准下，本条从"未执行"变为"已执行"。
- 帽 1/15 是**起点不是目标**：下调走脚本变更（天然 FULL 面）；`new StartupNoticeService` 的迁移（进 DI/端口）是减帽的自然形态，出本批范围。
- notes 通道保留（R1-S1 部分驳回）：站点级审计链是报告模式唯一的定位手段，16 行输出对 0.146s 门禁可接受；R1-S5 正则收敛挂账下次动 gate 时。
- 已知局限随脚本 docstring（如实声明）：collection-element `new()`、嵌套泛型、短别名限定不可见；误报走"改脚本即 FULL 评审"。
- cookbook 未记本条：`docs/cookbook.md` 字数预算 2761/2761 已满，加条即超限（留待预算腾挪时补）。

## Testing

- `verify-registration-discipline.py --self-test` 十夹具全过（合规注册/DI 放行/显式 new/目标类型 new/泛型 new/R1q 全限定/global-using 跳过/忽略标记/封顶）。
- `--enforce` 实仓绿：R1a 1/1、R1b 15/15；耗时 0.146s（hook 安全）；降帽（`--max-static-calls 14`）演示 trip，exit 1 且行可审计。
- 同批门禁：adr-format/budgets/md-links/governance/shell-standards/actionlint 全绿；`dotnet test` 未跑（零 src/tests 变更，按 diff 面最小证据）。
- 三重审核：R1/R2/R3 各首轮 0 Blocker（见文件头 Review 行与裁定）。

## Related

- 前序：[compose-root-gate](2026-09-28-compose-root-gate.md)（互补：根内构造归 C3，根外归本闸）与 [compose-root-form-separation](../architecture/2026-09-28-compose-root-form-separation.md)。
- 规范：[architecture-standards.md](../../../../docs/architecture-standards.md) R1/R4；方法论：上游实查范围——`deepseek-ai/deepseek-harness` 的 `packages/AGENTS.md` 作者规则、`scripts/check-workspace-constraints.ts`、`benchmark-next-package-dependency`、`dsh-code-review` skill（2026-10-01 经 gh 查源文件级核对）。
