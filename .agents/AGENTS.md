# AGENTS.md — .agents/（AI 协作层）

本子树的专属规则：**AI 协作机制本身**（技能、ADR、模板）。不重复根文件内容。

## 技能

- `.agents/skills/` 由 DSH 自动发现（skill 工具按需加载），无需注册。
- 本项目技能为**我们自己的**（方法论吸收上游 deepseek-harness 的通用实践，引用/命令/实现语言对齐本项目：.NET 单项目、XML doc、中文单语 + `.en.md` 镜像、`dotnet` 命令、`scripts/verify-*.py` 门禁）。技能格式由 `scripts/verify-skill-format.py` 机器强制（frontmatter/目录束/内链/结构，违约即 FAIL）。
- 原版 11 技能完整备份在 `.plan/种子/skills/`（本地，git 之外，供参考/复用），不进 `.agents/skills/` 活跃区。
- 用不上的技能不写（避免预铺空目录、避免上游断链踩坑）。

## ADR（Agent Notes）

- 路径：`.agents/notes/<lifecycle>/<class>/yyyy-mm-dd-<topic>.md`。
- class 封闭集合：`feature` / `bug-fix` / `simplification` / `architecture` / `process` / `testing`。
- 状态即目录：`proposed` → `implemented`（改 Status + 移目录）→ `archived`（冻结，只插 `Archived:` 行）。
- 文件名 = `yyyy-mm-dd-<kebab-slug>.md`：slug 小写连字符，日期为合法日历日且不晚于今日——命名格式由 `verify-adr-format.py` 机器强制（违约即 FAIL），新建即校验。
- implemented 笔记用现在时，**禁止** `## Proposal`/`## Plan`/`## Acceptance criteria`；强制 `## Alternatives considered`。
- 双语暂不启用：正文中文单语（文档层双语为 `.md` ↔ `.en.md` 镜像，见 `docs/`；ADR 正文不配对）。

## Cookbook（踩坑记录）

- 实现阶段踩坑与调试判别经验的单一事实源在 [`docs/cookbook.md`](../docs/cookbook.md)：每条带阶段标签（`[脚本]/[打包]/[调试]/[环境]/[上游]/[产品]` 封闭集），格式由 `scripts/verify-cookbook.py` 机器强制（违约即 FAIL），新增条目必须带标签与日期。

## 出处声明（MIT）

- 原版 `.agents/skills/` 11 技能：© deepseek-ai，MIT License，来自 `deepseek-ai/deepseek-harness`（https://github.com/deepseek-ai/deepseek-harness）。2026-08-20 自本机上游克隆原样拷贝（逐字节一致），**现备份于 `.plan/种子/skills/`**（git 之外，未被修改）。
- 本项目当前 8 个技能为**自研**（在 `.agents/skills/`）：方法论吸取上游，但写法/引用/命令为本项目版，故非上游逐字节一致。
- `.agents/notes` 骨架、`templates/`：来自 devops-template / deepseek-harness，MIT。搬运保留出处。
- 搬运修复：`scripts/verify-adr-format.py` 头部校验已按 deepseek 真实笔记约定修正（允许标题后空行再接 Status）——模板 Python 移植版原与他人为"标题/Status/空行"，与上游实际笔记（标题/空行/Status）冲突。2026-08-20 修正。
