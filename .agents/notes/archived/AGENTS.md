# Archived Agent Notes — 冻结区约定

> 上游 deepseek-harness 的 `archived/AGENTS.md` 未随技能搬运到本仓库；本文件即其本项目等价承载。
> 归档纪律的完整定义在 [../README.md](../README.md)（notes 契约）与
> [dsh-archive-agent-notes 技能](../../skills/dsh-archive-agent-notes/SKILL.md)。

- `archived/` 永久冻结：进入后不再编辑正文，仅 `Archived: <日期>` 行标识入档时刻。
- 归档笔记的外链按纪律不参与 `verify-md-links` 校验（链接腐化不回改）。
- 归档≠作废：含重引入条件/正典单点的笔记归档是为了降噪，重新启用先读正文再翻现行 ADR。
- 归档动作一律 `git mv`（保时间线）+ 插 `Archived:` 行 + 重跑 `verify-adr-format.py`。
