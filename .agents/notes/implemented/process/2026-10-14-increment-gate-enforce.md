# Agent Note: 增量纪律闸转正（`--enforce` 三接线 + 单文件帽只量既有文件）

Status: implemented

Review: FULL/2026-10-14/R1=ok R2=ok R3=ok

Related: 闸本体与前身 [`coupling-increment-baseline`](2026-10-12-coupling-increment-baseline.md)（n=100 分布、四帽值、三接线位）；三闸之母 [`coupling-shift-left-gates`](2026-10-01-coupling-shift-left-gates.md)。

## Problem

第②闸（PR 增量纪律）自 2026-10-12 起在 report 位运行：四帽只打 OVER 行、不拦路，转正条件写的是「一批 report 活数据后另起 FULL 批」。闸落地到 2026-10-14 之间的 live 批次（`f19f477` 之后三批：`f59e853` gross 52、`af25eb1` gross 576、`37b5e10` gross 6）里，`af25eb1`（上游锚点探测闸，已走 FULL 三审）打出唯一一条实质 OVER：**单文件净增 +362**，落在一个**新落盘**的闸脚本 `scripts/verify-upstream-anchors.py` 上。

这条 OVER 暴露的是判据面而非批次面：新文件没有「既有大小」可增长，其净增恒等于全文长度——既拆不动（没有可拆的旧内容），也压不掉（少写就是少功能），而「一次性塞进太多行」这一面 gross 帽已经覆盖。照原帽 `--enforce`，此类单文件新脚本会在 pre-commit 被本地拦下，出路会变成 `--no-verify`【推断 · 未证】——本地必拦而形态本身无可拆解，代价是绕闸而非拆批。

## Decision

1. **`--enforce` 三接线**：`.githooks/pre-commit`（`--staged --enforce`）、`.githooks/pre-push`（base 取 `git merge-base origin/main HEAD`，`--enforce`）、`ci.yml` docs job（`--since "$BASE" --enforce`）。OVER 从提示变为拦截；不可判定 diff（git 失败）的处置不变（原就 fail-loud）。
2. **单文件帽只量既有文件**：`file-max` 改由带 `--diff-filter=CDMRTUXB`（include-list 去掉 `A`）的 `--numstat` 计算，只算 BASE 时已存在的文件在本 diff 里的净增。gross 仍计新文件（新文件确实占评审面，只受 gross 约束），扇出/新包两帽口径不变。
3. **帽值不动**：600/150/15/1 沿用 n=100 的近 p90 线，转正不提帽。单文件帽换了度量总体，故同一 n=100 窗口（2026-09-28..2026-10-12 至 `d63833f`，逐提交 `git show --numstat`；分位取升序第 `int(0.9n)` 位，与基线篇口径一致）按新口径复测：**file-max p90=89 / max=187**（n=99——`0fc20b0` 的 diff 全为新增文件），且同窗口复现旧口径的 gross p90=545、file-max p90=170/max=360。150 仍落在 p90 与 max 之间。
4. **过帽的出路仍只有一条**：拆批，或在 ADR 记录理由（OVER 行原文即 "split the PR or justify in the ADR"）。闸只拦体积，不判质量。

## Alternatives considered

- **照原帽直接 enforce（新文件也计入 file-max）**：落败——live 反例即本批的前置：`af25eb1` 的新脚本 +362 是 FULL 审过的正当批次，而新文件的净增无法通过拆/压消解，拦它等于拦「新落一个脚本文件」这一形态本身。
- **只提帽不改测量面（单文件帽提到 400）**：落败——新口径同窗口 max=187，提到 400 是空闸；且新文件的净增恒等于全文长度，提帽不治「新文件混入 file-max」这层语义。
- **只 enforce 三帽、单文件帽留 report**：落败——既有文件的膨胀（真正可拆、可修的那面）就不再被拦，而它恰是该帽唯一有依据的对象。
- **继续 report 位、再攒一批数据**：落败——触发面已满足（live 数据在案），唯一 OVER 已定性；再等只是拖延，语义混淆不会自愈。
- **让新文件在 gross 里也打折**：落败——新文件照样占评审面，gross 该计它们；「既有文件的增长」才是可拆性所在。

## Consequences

- 三处执行点同帽同口径：pre-commit 拦提交、pre-push 拦推送（base 取 merge-base，origin/main 已前进时不把上游提交算成本批的删除行）、CI 拦合并（base 取事件载荷的上一 tip / PR base——该 ref 是 HEAD 祖先时两点与三点等价）。
- 已知边界（沿用前篇）：多 commit 区间下单文件净增为区间累计、帽按单 PR 校准——`--since` 用于多提交区间是近似，单批口径以 CI 的 base 为准。
- 已知盲区：改名+大改在相似度 <50% 时被 git 拆成 `D`+`A`，其净增落 A 侧而被过滤；区间内先新增再修改的文件相对 BASE 恒为 `A`，同样不入 file-max。两者都由 gross 兜底。

## Testing

- `python3 scripts/verify-increment-discipline.py --self-test` 8 夹具：新增两条离线夹具（既有文件过滤参数 `_numstat_args`；`_sizes` 下新文件入 gross、不入 file-max）与一条**临时 git 仓库端到端夹具**（既有文件 +80 / 新文件 +400 / 改名+大改被拆成 D+A，断言 file-max 只取 +80）。负控已验：过滤常量改成小写 `--diff-filter=d`（git 小写 = 排除列表，语义反转）时该夹具判红（`file_max=400 top=new.py`）——argv 夹具那种「常量自比」抓不到这一形。
- live 复测（本机实跑，2026-10-14）：闸落地后逐批 gross 52 / 576 / 6（`git show --numstat` 统计，与 Problem 同源）；`af25eb1`（上游锚点批）改前红在 `file-max +362`，改后 `--since af25eb1^ --enforce` → `OK`（exit 0）。跨三批的 `--since f19f477` 如实**不是全绿**：gross 634 > 600（区间累计，见上「已知边界」）、`file-max(existing)` +5（`AGENTS.md`）——新脚本 +362 不再入该帽，但跨批区间不代表单批通过。`--since HEAD~100 --enforce` → 4 OVER / exit 1（红路径仍可达）。
- 机器门禁：adr-format / cookbook / doc-budgets / md-links / shell-standards（含 hook 改动）/ governance / handoff-structure / code-health / code-conventions / compose-root / registration-discipline / review-tier 全绿；`actionlint` 本地缺装，workflow 改动由 CI 真 runner 覆盖。
- dotnet 面零触碰（纯脚本/hook/workflow/文档）：`dotnet build/test/format` 沿用 `37b5e10` 绿（基线 `1042/1042`、覆盖率 63.59%，见 `scripts/test-baseline.json`），本批不重跑全量（改动面未达 src/tests，见 `scripts/change-scope.sh`）。
