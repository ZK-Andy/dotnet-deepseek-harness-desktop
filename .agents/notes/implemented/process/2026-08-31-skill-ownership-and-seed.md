# Agent Note: skill-ownership-and-seed（技能自研——备份原版、按本项目写自己的、加机械验证）

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`.agents/skills/` 里的 11 个技能是 deepseek-harness **上游原版**，其 sources-of-truth / repo-context 清单指向上游 DSH 仓库文档（`packages/AGENTS.md`、`docs/defensive-patterns.md`、`docs/AGENTS.md`、`docs/subsystems/`、`docs/i18n/`、多篇上游 notes 示例），在本仓库（单项目 .NET 桌面壳）**几乎全部断链**。这些断链被 `verify-md-links.py` 掩盖（默认排除 `skills/`），故藏而不露。

三重评审实际调用 `dsh-code-review` / `dsh-find-simplifications` 时，评审代理按技能默认清单走会引到不存在的文档——**评审实操会踩坑**。此前尝试「给技能打本项目补丁」被否（污染上游原版可复用性）；「保留断链、仅靠上层兜底」也被否（评审实操仍不可用）。真正的解法是：**本项目不依赖上游技能，写我们自己的**。

## Decision

**技能自研**：备份原版 11 技能到种子（git 之外），按本项目真实需要写我们自己的技能，并加机械验证防写偏。

### 1. 备份原版到种子

原版 11 技能完整拷贝至 `.plan/种子/skills/`（本地工作文档，git 之外，未被修改），供参考/复用/未来项目取方法。它们**不进 `.agents/skills/` 活跃区**。

### 2. 按本项目写我们自己的技能（8 个）

方法论吸收上游 deepseek-harness 的**通用实践**（完整命题、CoT 泄漏八类、ADR 归档、最小证据、评审清单），但**引用/命令/实现语言对齐本项目**：

- .NET 单项目（无 `packages/`、无 `pnpm`、无 `knip`、无 Cordis/schemastery）——引用改为本项目 `AGENTS.md` / `docs/coding-standards.md` / `docs/architecture-standards.md` / `.agents/notes/README.md` / `scripts/verify-*.py`。
- XML doc（`<summary>/<param>/<returns>`）而非 JSDoc。
- `dotnet` 命令而非 `pnpm`；`scripts/change-scope.sh` 而非 `pnpm run change-scope`。
- `.en.md` 双语镜像（README/user-guide/faq）而非上游 `.zh.md`/`.i18n.yaml`/hash 三件套。

写 8 个：`dsh-code-review`、`dsh-find-simplifications`、`dsh-archive-agent-notes`、`dsh-pre-push-checks`、`dsh-prose-standard`、`dsh-trim-cot-leakage`、`dsh-doc-standards`、`dsh-translate-docs`。

### 3. 不写 / 删除不适用技能

- `dsh-doc-site-sync`（无 VitePress 文档站）、`dsh-merging-stacked-prs`（顺序合并，无 GitHub stack）、`record-browser-gif`（GUI 录屏暂缓）——从 `.agents/skills/` 移除（已备份到种子），避免断链踩坑。

### 4. 加机械验证（防写偏）

新增 `scripts/verify-skill-format.py`，校验每个 `.agents/skills/*/SKILL.md`：frontmatter（`name` 小写 kebab + `description` 非空）、目录束（dir basename == name）、相对 md 内链（文件存在 + 锚点有效，**强制校验 skills/** 不像 verify-md-links 那样跳过）、结构（含 "guidance, not a script" 定位 + `## Workflow` 段）。接入根 `AGENTS.md` 质量门。

## Alternatives considered

- **给上游技能打本项目补丁（修断链）**：污染上游原版可复用性、收益仅是默认不跑的校验。落败（此前已试、已撤）。
- **保留断链、仅靠上层兜底（feature-flow 源点映射）**：评审实操仍会引到不存在的文档，不可用。落败。
- **全 11 个都改写成我们自己的**：其中 3 个（文档站同步/栈合并/GUI 录制）本项目无对应生态，写完也用不到。落败；只写真实需要的 8 个，不预铺空目录。
- **不写我们自己的、直接用上游原版**：断链 + 踩坑。落败。
- **种子文档进 git**：用户拍板「种子不进 git」（铁律），种子含技能备份留在 `.plan/`（本地）。

## Consequences

**收益**：
- 本项目 `.agents/skills/` 8 个技能**自研**：引用/命令/语言全部对齐本项目，无断链，评审实操不再踩坑。
- 原版 11 技能完整备份在种子（git 之外），可复用、可做方法来源。
- `verify-skill-format.py` 机械强制技能格式——新技能不会写偏（frontmatter/目录束/内链/结构合规才通过）。

**代价**：
- 技能体系从「上游原版」变为「自研」——不再随上游自动同步；需自行维护。缓解：方法论源头仍在种子（`.plan/种子/skills/`），需要时可参考/取方法。
- 8 个技能内容基于本项目当前规则，项目规则演进时需同步更新技能。

**Testing**：技能格式变更不引入独立测试；`verify-skill-format.py` 经 `--self-test`（frontmatter/name 正则断言）验证；对 8 个技能跑 `verify-skill-format.py` 全绿（OK: 8 skills conform）。门禁 `verify-adr-format` / `verify-doc-budgets` / `verify-md-links` 全绿；`.agents/AGENTS.md` 预算 258/300 通过。

## Related

- 前序尝试（技能回上游原版 + 上层规则主导）未独立归档——其核心 rationale「**不给上游技能打本项目补丁**」已并入本 ADR 的 `Alternatives considered`（「此前已试、已撤」）。当前生效方案 = 自研技能 + 备份原版 + `verify-skill-format.py`。
- `.plan/种子/skills/`：原版 11 技能备份（git 之外）。
- `scripts/verify-skill-format.py`：机械验证脚本。
