# Agent Note: 评审兜底清单机械化——analyzer 化（D001/D002）+ UI 文案单一词典 + 元规则

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

上游对齐台账（`.plan/upstream-alignment-plan-2026-09-13.md` §2.1/§2.2）定位的差距：本仓 AGENTS.md「评审检查项（AI 兜底）」收容了多项在 .NET 有机器化路径的规则——D001（`Async` 尾缀）、D002（禁 `async void`）可用现成 analyzer 进 build 门禁；UI 文案散在 C# 字面量与 `wwwroot/index.html` 两处、无门禁核对；且没有一条元规则防止「评审兜底」清单只增不减（对照上游方法论：机器可查的不变量必须接进可执行门禁，规范语料按「能否机器化」预过滤）。

## Decision

三件事一批（同一主题：评审兜底→机器门禁）：

1. **D001/D002 analyzer 化**：主工程加 `Microsoft.VisualStudio.Threading.Analyzers`（PrivateAssets），`.editorconfig` 激活 `VSTHRD200`/`VSTHRD100` 为 warning；该包误报面大的并发误用规则（VSTHRD002/003/103/110，共 34 处既有告警，属壳的组合根/监督同步等待既有设计）逐条 none。既有违例清零（`RunOnce`→`RunOnceAsync`、`WithStepTimeout`→`WithStepTimeoutAsync` 重命名）。D003（空 catch 命名所吞）无现成 analyzer，仍留评审兜底，自定义 analyzer 试点另立项。
2. **UI 文案单一词典（批次 A）**：新增 `Services/UiCopy.cs` 为用户可见文案的唯一家（托盘/横幅/恢复页/过渡页/引导页登记）。选型强类型静态类而非 resx：本壳仅中/英两分支、由 `UiLocale.IsEnglish` 判定，resx 卫星装配对两分支是纯开销，强类型入口以编译器代替字符串键保证引用完整。`TrayMenuActions`/`UpdateBanner`/`UiLocale.OkLabel`/`RecoveryPageBuilder`/`DesktopBanner` 缺省按钮全部改走词典（零行为变更，612/612）。静态引导页 `index.html` 为被核对的消费方：其中文文本块必须在 `UiCopy.cs` 登记常量。
3. **新门禁 `scripts/verify-ui-copy.py`**：两条不变量——UI 消费文件（清单在脚本头）零 CJK 字面量（`[host]` 等 log 形态字面量豁免：诊断行不属 UI 文案）；`index.html` 中文文本块子串命中 UiCopy 登记串。接进 CI 文档门禁 job 与 AGENTS 质量门清单，`--self-test` 正反三例。
4. **元规则入 AGENTS.md（批次 D）**：新规范默认问「能不能进 verify/analyzer」，机器化不了的才落评审兜底，清单只减不增——D001/D002 随本批移出兜底清单，D003 条目收窄。
5. **随车项（§1.1 #2 核对收口，真实行为变更，非零行为）**：`EnvironmentHygiene` denylist 补 `NODE_PATH`——上游 `host-process.ts`/`project-manager.ts` 两处 spawn 均剥此键，本壳漏剥会让子进程继承宿主 `NODE_PATH`（上游 node 模块解析污染面）。已补齐并加用例；本批其余迁移均零行为变更，此项是唯一子进程可见差异。

## Alternatives considered

- **resx 词典**：落败——卫星装配/ResourceManager 文化链对两分支场景是纯开销；且 verify-ui-copy 需解析 resx XML，强类型类源码即词汇表，门禁实现更薄。
- **VSTHRD 全家激活**：落败——VSTHRD002/003/103/110 在组合根同步等待（`.GetAwaiter().GetResult()` 的启动装配面）与监督路径共 34 处既有告警，属既有设计而非缺陷；一并清零等于一次大 diff 的语义冒险，违背「门禁批零行为变更」。
- **index.html 改由 C# 生成**：落败——静态文档自包含（崩溃/引导期 dsh 必不可用）是既有约束；生成化收益低且引入渲染耦合。登记+核对以最小代价达成单一事实源。
- **verify-ui-copy 全仓扫 CJK 字面量**：落败——host.log 诊断行遍布全仓且合法；UI/诊断边界以「消费文件清单 + log 形态豁免」表达，清单即扩展点，误报面收敛到零。

## Consequences

- D001/D002 违规自本批起 build 即 fail loud；评审兜底清单缩至 D003/R1/R3/IPC 强类型。
- 新增 UI 文案的动线：先进 `UiCopy.cs`，消费文件只写引用；`index.html` 改文案必须同步登记，否则 CI 拦截。
- 后续候选：恢复页/引导页的英文分支接线（`UiCopy` 已备双参入口，现传 `english: false` 保持现状）；D003 自定义 analyzer 试点。
