# Agent Note: adopt-deepseek-harness-ai-collaboration-framework

Status: implemented

## Problem

空白新项目缺少"人 + AI 协作开发"的工作方式：没有给 coding agent 的规则层、没有决策记录系统、评审/push 无纪律、规范无机器强制。若直接写产品代码，决定会散在会话里、规范只是纸面约定。

## Decision

从 deepseek-harness 开发体系（经 devops-template 提炼）引入**第一步：通用适配**，在本仓库建立与技术栈无关的 AI 协作骨架——此决策**不涉及**产品技术选型（UI / target framework / README / LICENSE 属第二步）：

- 根 `AGENTS.md`（分层指令 + 硬规则 + 字数预算）+ `.agents/AGENTS.md`（协作层子树规则）。
- `.agents/skills/` 全 11 个技能，deepseek-harness 原版原样（引用保留为参考链接；`verify-md-links` 默认排除 skills/）。
- `.agents/notes/` ADR 系统：`{lifecycle}/{class}/yyyy-mm-dd-<topic>.md`，强制 `## Alternatives considered`，implemented 禁 spec 用语，archived 永久冻结。
- `templates/adr-*.md` + `scripts/verify-adr-format.py` / `verify-doc-budgets.py` / `verify-md-links.py` + `scripts/change-scope.sh`。
- 双语暂不启用：README/正文中文单语，架构上预留配对机制。

## Alternatives considered

- **只搬技能、不建 AGENTS.md/ADR/门禁**：落败——技能触发靠规则层和上下文，无决策记录与门禁则体系退化成"装着没用的工具箱"。
- **一次性连第二步一起做**（README/LICENSE/.NET 初始化/CI）：落败——环境未定，技术栈先定了会返工；用户口径明确区分"通用适配"与"项目专属"两步。
- **只放 7 个核心技能**：落败（暂缓）——环境未定、首版后必有清理，全 11 原版按描述触发零干扰，用证据剪枝比现在预测更准（含 browser-gif 用于 GUI 阶段、merge-stacked 策略未定）。

## Consequences

- 收益：agent 一进仓库即有规则可循（DSH 实测自动加载根与 .agents 的 AGENTS.md）；决定进 ADR 可评审可回放；规范由脚本强制而非纸面。
- 代价：当前质量门需手动跑（hooks/CI 属第二步）；技能正文保留的上游路径对本仓库是参考链接，不逐条重写；`rejected`/`archived` 暂无内容，待系统积累。
- 里程碑：首版可运行后做**理念复盘**——对 imported 体系逐条"保留/改造/放弃"回写 `docs/理念沉淀.md`（工作区级）与 process ADR；技能按证据清理。
