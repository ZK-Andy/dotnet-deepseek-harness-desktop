# Agent Note: todos-cold-archive

Status: implemented

Review: FULL/2026-09-16/R1=ok R2=ok R3=ok

## Problem

`HANDOFF-todos.md`（行动区，gitignore 本地）的完成项生命周期只有「压缩」没有「移出」：2026-08-31 拆分治理（`handoff-split-governance`）把 `[x]` 定义为 ≤220 字一行指针并给出总条数上限 70，但没有为越龄指针定义去向。后果在 2026-09-16 实测显形：

- 69 条 `[x]` + 1 条 `[ ]` = **70 条，精确顶格**——下一条完成项入库即越过条数预算、门禁 FAIL；此时唯一出口是删旧条目或抬额度，都不是「归档」。
- 姊妹文件已有归档通道，行动区没有：`HANDOFF.md` 的「交接更新记录」是 ≤24 条有界滚动窗，越窗条目归档至 `.plan/journal/<YYYY-MM>-session-journal.md`。
- 观感代价：69 条 `[x]` 全部堆在「## 进行中（[ ]）」标题下，「## 已完成」区形同虚设；人读到「一堆事仍在进行中」，而实际开放项只有 1 条。

journal 不能兼任该去向：它的单一职责是**会话叙事**（发生了什么），完成项指针是**行动轨迹索引**（做过哪些事）；混卷会让 docs-management-tiering 建立的「叙事 vs 决策」分层再次糊掉。

## Decision

**给 `[x]` 指针加一条与滚动窗对称的冷归档通道**（用户 2026-09-16 拍板）：

1. **窗口**：`HANDOFF-todos.md` 只保留最近 ≤24 条 `[x]` 一行指针（`--max-closed`，默认 24，与 HANDOFF.md 滚动窗同量级同口径：近期会话批次）。24 是上限而非目标——裁剪按**日期边界**进行，不拆同日组（当前窗口起点 2026-09-14）。`[ ]` 窗口与其字符预算的既有家是 `handoff-split-governance`，本笔记不复述。**总量预算由两条分状态窗口承担**（`[ ]` ≤16 + `[x]` ≤24 → 总上限 40）：不设独立的总量闸，避免出现够不着的预算读数。
2. **冷归档卷**：越窗指针整体移入 `.plan/todos-archive/<YYYY-MM>-todos-archive.md`（gitignore 本地，按月分卷，与 journal 同命名习惯）；卷内保留每条的完整一行指针（日期/commit/ADR），只换存放位置、不丢索引价值。
3. **归档前先对账未决残留**：越窗 `[x]` 若仍带未决事项（待复验/待上游/待真机/条件触发挂账），先提升为 `[ ]` 再归档——冷归档卷只存已完成轨迹，活不随完成项一起下沉。
4. **指针**：`HANDOFF-todos.md` 末尾留「## 冷归档」一节，一行指针指向当前卷。
5. **门禁**（`verify-handoff-structure.py`）：`[x]` 超窗即 FAIL 并指名归档卷路径；待办文件与归档卷**成对**校验——文件点名的每个卷必须存在，且 `.plan/todos-archive/` 有卷时文件必须点名（指针被删/改写成不可识别形态不能让卷静默成孤儿）。卷解析锚在 HANDOFF 目录，与 journal 卷同锚点。
6. **流程卡同步**：`session-close` 步骤 3 增「勾销后 `[x]` 越窗即归档并先对账残留」；`session-open` 步骤 1 说明 `[x]` 是近期窗口、越窗指针在冷归档卷。

## Alternatives considered

- **直接删除越龄 `[x]` 行**（承认 ADR/cookbook/git log 已是轨迹家）：落败——`[x]` 指针是「这条做过」的单一索引入口，删除后跨会话检索某段工作是否落过只能翻 git log 与 ADR 目录；归档保留同一可读性，成本仅多一个本地文件。
- **只抬条数预算**：落败——`handoff-split-governance` 在 `Alternatives considered` 已判「仅放宽预算」为治标，该 ADR 的 Problem 正是「膨胀压力从未被机制真正约束，只是被推迟」。
- **越窗指针并入 `.plan/journal/` 月卷**：落败——journal 担会话叙事，完成项指针是行动轨迹；混卷破坏「叙事 vs 决策」分层，且让「一个事实只有一个家」失效。
- **归档进 git 跟踪的 `docs/`**：落败——HANDOFF 家庭整体 gitignore 是既有决定（本地工作文档不提交）；进 git 会把高频易失的会话轨迹变成仓内噪声与文档预算负担。
- **越窗行原样归档，残留留在卷里**：落败——残留是活事项，随完成项沉入冷卷后「读热区」的会话直接漏项，正是本决策要消灭的失效模式；先提升为 `[ ]` 的成本仅几行。
- **窗口按时间（如保留 30 天）而非条数**：落败——时间窗在低频会话期让文件长期膨胀、在密集期又过早归档，且门禁要解析相对时间；条数窗与 HANDOFF.md 滚动窗口径一致、判定无歧义。

## Consequences

- **现状基线**：`HANDOFF-todos.md` 19 条（8 `[ ]` + 11 `[x]`），另 58 条在 `.plan/todos-archive/2026-09-todos-archive.md`；「进行中」区只含开放项，其中 7 条由越窗完成项的未决残留提升而来。
- **预算与生命周期**：完成项现有两段生命周期——近期窗口（≤24，热）→ 冷归档卷（按月，冷）；越窗指针离开热文档，额度不受长期累积挤压，也无须靠删除维持。
- **风险与缓解**：窗口过小丢近期上下文 → 24 与 HANDOFF.md 滚动窗同口径（近期两个会话批次），冷归档卷仍可检索；归档卷无预算约束、长期增长 → 与 journal 同级接受（本地易失层，快照触发条件见 `docs-management-tiering` 第 3 层）。
- **门禁误伤**：`--max-closed` 可 CLI 覆盖；指针存在性校验仅在指针出现时触发，无归档历史时不生效。

## Related

- `handoff-split-governance`（implemented/process/2026-08-31）：本笔记补齐其留下的「待办区无生命周期」缺口；`[ ]` 窗口（16）与字符预算的既有家仍是该 ADR。
- `docs-management-tiering`（implemented/process/2026-08-25）：叙事/决策分层的既有决策，本笔记把「滚动窗 + 归档卷」通道对称复制到行动区。
- `verify-handoff-structure.py`：本决策的机器落点。
- `.agents/workflows/session-open.md` / `session-close.md`、`AGENTS.md` 质量门：引用同步落点。
- `HANDOFF-todos.md`、`.plan/todos-archive/`（gitignore 本地）：被治理对象。
