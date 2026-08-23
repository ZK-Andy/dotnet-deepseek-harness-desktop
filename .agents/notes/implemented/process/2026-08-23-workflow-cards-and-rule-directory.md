# Agent Note: workflow-cards-and-rule-directory

Status: implemented

## Problem

开发工作流每次会话凭记忆临场拼装，漂移实例：①会话模式无契约——讨论/实现边界靠默契，曾出现讨论型会话提议开工、顺手改配置的越位；②收尾无检查单——共享 home 三条提交未进 HANDOFF，下个会话险些误读决策；③发版、评审、运行时升级等链路步骤散落在 HANDOFF 历史叙事中，每个会话重新拼装；④规则细则曾整段塞进根 AGENTS.md（383 词），违背本仓「每个事实只有一个家：procedure → cookbook；规则 → 本文件 + 链接」的家规。

## Decision

流程固化三层落地：

1. **模式契约**（session-modes）：讨论 / 调研 / 实现 / 发布四型，各带许可边界与禁区；会话开场必须声明模式，切换须显式，边界争议停问不扩权。
2. **命名流程卡**：`.agents/workflows/` 五张检查单——session-open / session-close / feature-flow / release-flow / session-modes。内容为活文档，「使用中磨合」迭代，修订随普通提交走。
3. **规则目录化**：根 AGENTS.md 只留一行级强制触发语 + 相对链接；操作配方迁 `.agents/workflows/` 细则文件（链接受 verify-md-links 校验）。

首批随卡入册的两张纪律：GitHub 调研六步配方（gh CLI 强制，禁 web 开局与全量克隆首选）；检索通道路由（AnySearch 为唯一搜索后端且已 Provider 级接管内置 web_search/web_fetch，规则只区分工具面：zone/纵向用 anysearch_*，快查可用裸 web_search）。

## Alternatives considered

- **仅以 skill 承载全部流程**：落败——skill 惰性加载可能整个会话不触发，约束力最弱；触发语必须常驻上下文（根 AGENTS.md），细节才外链文件。
- **细则全塞根 AGENTS.md**：落败——违背「AGENTS.md 是目录」定位，字数预算被命令配方挤占（曾达 383 词且仍在涨）。
- **机械门禁管对话内行为**：不可行——门禁只能拦有产物经过检查点的行为；检索通道选择无产物可 lint，硬造仪式成本大于收益。

## Consequences

- 「讨论型会话零代码写入」从默契变明文；文档类决策记录仍是讨论型的本职产出。
- 强制天花板如实声明：行为类规则的约束 = 根 AGENTS.md 常驻注入 + 用户观察点名；若漂移反复，再评估升级为 MCP 工具形态（工具列表每轮可见）。
- 流程卡按需增删：新流程先立卡再执行，废弃的卡删除不留桩。

## Related

- [session-modes](../../../workflows/session-modes.md) / [session-open](../../../workflows/session-open.md) / [session-close](../../../workflows/session-close.md) / [feature-flow](../../../workflows/feature-flow.md) / [release-flow](../../../workflows/release-flow.md)：五张卡本体。
- [github-research](../../../workflows/github-research.md) / [search-routing](../../../workflows/search-routing.md)：首批纪律卡。
