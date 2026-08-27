# Agent Note: workflow-and-gate-optimization-round

Status: implemented

## Problem

HANDOFF「交接更新记录」随会话累积膨胀（271 行/7494 词），根因有三：叙事流水账无家可归（不属于 ADR/cookbook/README/AGENTS 四家）；HANDOFF/.plan 均 gitignore，完全不被任何门禁校验（不进 doc-budgets/verify-*）；`session-close` 要求「追加详细交接条目」持续喂它。上一轮已把 HANDOFF 结构拆分为「有界滚动窗口 + journal 归档」（`docs-management-tiering` implemented），但**机器兜底仍未落地**：滚动窗条数、状态区完整性、journal 指针都靠自觉；且 ADR 活动面里残留一批**一次性版本 bump/已完成缓存机制**的 process 笔记，对未来决策无杠杆却增加每会话读噪声。

## Decision

四件套同批落地（工作流/门禁/ADR 瘦身，一致收口）：

1. **新增 `verify-handoff-structure.py` 门禁**——给 HANDOFF 这一 gitignore 层补机器兜底：
   - 「交接更新记录」滚动窗条数 ≤ 默认 40（超了 forced 归档最旧条目）；
   - 必需状态区齐全（背景/位置/当前状态/待办/开始步骤，容忍括号后缀）；
   - 存在 journal 归档指针且被引用卷落盘 `.plan/journal/<YYYY-MM>-session-journal.md`。
   - `--self-test` 5 用例；HANDOFF 缺失（清检 CI）静默通过。接入 pre-commit + ci.yml + 根 AGENTS.md 质量门。
2. **`verify-md-links` 排除 `archived/`**——归档笔记外链按纪律永不校验（冻结历史，见 `.agents/notes/README.md`）；排除限定在 `.agents/notes/` 下的 archived（避免未来误伤无关目录）。原脚本只排除 `skills/` 与生成物目录，漏掉归档层；`.plan/` 保持可扫描（其内部无 `.plan/` 相对链接）。
3. **ADR 归档瘦身**——依 `dsh-archive-agent-notes` 语义分类（子代理），6 篇一次性版本 bump/已完成缓存机制（其后行为已由现役脚本/更新 ADR 承载，无独有 durable 约束）归档 `archived/process/` 并插 `Archived: 2026-08-27`：cache-assembled-runtime-closure、pnpm-store-ci-cache、upgrade-bundled-dsh-rc8、upgrade-bundled-dsh-011-rc1、upgrade-bundled-dsh-rc2、runtime-deps-upgrade-and-arm64-resume。**保留 4 篇含重引入条件/正典单点/所有权边界**：pin-pnpm-1170（上游修复 peer 前不可放宽钉版）、freshness-pin-patrol（LTS-not-Current + preflight warn-only 语义）、pin-source-convergence（单一来源所有权边界）、strip-sourcemap-comments-in-closure（**裁剪行为变更必须 bump TRIM_POLICY**）。顺带修复指向这些已归档笔记的 3 处入链（链接重定向至 archived/ 路径）。实施后 ADR 从 58 → 52，process 活动面 21 篇。
4. **流程卡微调**（依子代理审查，含必修点）——`session-open`：步骤 1 读「状态区+滚动窗」（原「读全文」），修 Gotchas 死引用（原 Gotchas 节已迁 cookbook），门禁基线补第 4 脚本，`日期卷`→`月卷`；步骤 6 同修 Gotchas 死引用。`session-close`：步骤 2 删「/ HANDOFF」（结论只落 durable 四家）；步骤 3 钉成「跨会话遗留/复验观察的唯一落点」；步骤 7 收紧为「精条目 + 指向 ADR + 保留提交哈希 + fail-loud 前置（未落 ADR 不得代替）」；步骤 8 明示 A/B/C/D 为「速览视角、非穷尽法规」。

## Alternatives considered

- **只删新条目靠自觉**：落败——无机器兜底，会话一忙即复发（HANDOFF 膨胀原始成因）。
- **HANDOFF 全进 git 跟踪让现有门禁覆盖它**：落败——违背「本地工作文档不提交」既有决定；且 md-links 会误扫其内部 `.plan/` 引用。
- **archived/ 纳入 md-links 校验**：落败——与归档纪律冲突（归档笔记外链冻结不校验），且归档文本多为旧路径会持续红。
- **6 篇全归档 + 4 篇保留全保留（不分类）**：落败——4 篇含重引入条件/正典单点（pin-pnpm / freshness / pin-source-convergence / strip-sourcemap），归档会丢未来变更必须 consult 的约束。
- **归档笔记不修入链**：落败——3 处 git-tracked 入链会变死链（md-links 红），必须重定向至 archived/ 路径。
- **流程卡只改步骤 7**：落败——不改步骤 2/3 则「拔结论」会制造「漏写 ADR 就丢结论」与「遗留事项断头指针」两个新坑（审查实证）。

## Consequences

- 收益：HANDOFF 膨胀有机器兜底（滚动窗超界即红）；ADR 活动面收敛（58→52，process 26→21），每会话读噪声下降；流程卡与「叙事 vs durable 分离」完全一致，不再自行制造流水账。
- 代价/风险：verify-md-links 目标数从 122 → 121（排除 `.agents/notes/archived/`，含新 ADR Related 链接）；`archived/` 现含 6 篇冻结笔记，其外链不再校验（符合纪律）；HANDOFF 状态区仍 gitignore 裸奔，但滚动窗条数/指针已被本门禁兜底。
- 「每个事实只有一个家」在会话层的完整落点：durable 结论 → ADR/cookbook/README/AGENTS；叙事轨迹 → `.plan/journal/`；当前事实 → HANDOFF 状态区；未完成项 → HANDOFF 待办区；滚动窗只做有界指针。
- 评审（三路并行，只读）：R1 简化——2 硬死代码（删死三元 `root != "."`、删未用 `import datetime`）+ 1 弱（journal 卷名提常量）全采纳；R2 代码——Blocker B1（`--handoff` 绝对路径时 journal 解析到 cwd 致误红）已修（改为 `handoff.parent/.plan/journal/`），建议 I1（`archived` 排除收紧到 `.agents/notes/` 下）采纳，顺手修既有 `desktop-shell-self-update` 的「HANDOFF Gotchas」死引用；R3 归档——6 篇归档/3 处入链/保留-归档分界全合规，`## Related` 改相对链接。无 blocker 遗留。

## Related

- [docs-management-tiering](2026-08-25-docs-management-tiering.md)：本批是其第 1 层落地的机器兜底补齐。
- [.agents/notes/README.md](../../README.md)（archived 规则）/ [docs/cookbook.md](../../../../docs/cookbook.md)（踩坑判别要点落点）。
- [session-close](../../../workflows/session-close.md) / [session-open](../../../workflows/session-open.md)（本批微调的流程卡）。
