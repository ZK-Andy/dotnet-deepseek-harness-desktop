# AGENTS.md — .agents/（AI 协作层）

本子树的专属规则：**AI 协作机制本身**（技能、ADR、模板）。不重复根文件内容。

## 技能

- `.agents/skills/` 由 DSH 自动发现（skill 工具按需加载），无需注册。
- 技能为 deepseek-harness 原版；**正文不改方法论**——本项目要加的专属规则写进上层 AGENTS.md 或本文件。
- 用不上的技能不会触发，零干扰；首版后按方案§5.1 以证据清理。

## ADR（Agent Notes）

- 路径：`.agents/notes/<lifecycle>/<class>/yyyy-mm-dd-<topic>.md`。
- class 封闭集合：`feature` / `bug-fix` / `simplification` / `architecture` / `process` / `testing`。
- 状态即目录：`proposed` → `implemented`（改 Status + 移目录）→ `archived`（冻结，只插 `Archived:` 行）。
- implemented 笔记用现在时，**禁止** `## Proposal`/`## Plan`/`## Acceptance criteria`；强制 `## Alternatives considered`。
- 双语暂不启用：正文中文单语（开启时恢复 `.zh.md` + 配对机制）。

## 出处声明（MIT）

- `.agents/skills/` 全部技能：© deepseek-ai，MIT License，来自 `deepseek-ai/deepseek-harness`（https://github.com/deepseek-ai/deepseek-harness）。2026-08-20 自本机上游克隆原样拷贝（逐字节一致）。
- `.agents/notes` 骨架、`templates/`：来自 devops-template / deepseek-harness，MIT。搬运保留出处。
- 搬运修复：`scripts/verify-adr-format.py` 头部校验已按 deepseek 真实笔记约定修正（允许标题后空行再接 Status）——模板 Python 移植版原与他人为"标题/Status/空行"，与上游实际笔记（标题/空行/Status）冲突。2026-08-20 修正。
