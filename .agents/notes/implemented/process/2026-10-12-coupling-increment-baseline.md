# Agent Note: 耦合增量闸基线与 report 闸

Status: implemented

Review: FULL/2026-10-12/R1=ok R2=ok R3=ok

Erratum: 2026-10-15 — Decision 1 的「R1b=11/11（零余量如初）」与 Consequences 的「R1b 回到零余量」已失准：静态域入口实例化后实仓 **9/11**（[sequencer-resolution-injection](../architecture/2026-10-15-sequencer-resolution-injection.md)）；帽值 11 未动，余量为 2。正文与决定不动。

## Problem

耦合左移三闸只落了第①闸（注册点扫描）：第②闸（PR 增量闸：依赖扇出 diff + 单文件单 PR 净增上限，对标上游 `benchmark-next-package-dependency`）缺分类记账与基线分布。记账半（n=15）已落本篇 proposed；执行半需扩样、定帽、落闸、转（半）执行。

## Decision

1. 分类法五维（判据 + 本仓当前计数，2026-10-12 实测）：①注册外直构 R1a=1/1 ②注册外静态调用 R1b=11/11（本批降帽，零余量如初）③层内跨域引用（零机器覆盖，占位）④BCL 直调 D005（零命中）⑤单 PR 净增膨胀（本闸覆盖）。
2. 基线分布（n=100，2026-09-28 至今全量提交）：gross p90=545/max=1812，净增 p90=+362/max=+618，单文件净增 p90=170/max=360；跨层扇出（短名法，注释/字符串/using/同层剔除）27/100 非零、max=76；新增包引用 1/100（本仓 D003 批自身）。token 字面法已实测证伪（n=100 得 0/100——global using 下代码面恒无 `Infrastructure.` 字面）。
3. 闸 `scripts/verify-increment-discipline.py`：四 metric（gross/单文件净增/跨层扇出/新包）+ 帽（600/150/15/1，近 p90 报告线）+ `--staged`（pre-commit）/`--since`（CI）/默认 worktree + `--self-test` 8 夹具（numstat/既有文件过滤参数/新文件不入 file-max/临时仓库端到端/扇出纯度/多引用逐计+`+++`头/in-diff 声明/四帽齐红）。四帽同帽比较、三处接线均带 `--enforce`（转正经过与单文件帽只计既有文件的语义见 [increment-gate-enforce](2026-10-14-increment-gate-enforce.md)）。接线：pre-commit `--staged`（毫秒级）+ pre-push（base 取 `git merge-base origin/main HEAD`，outgoing 早信号）+ CI `--since $BASE`（与 review-tier 同 base；无 base 报 warning 跳过）+ CI 自测块。
4. R1b 帽 15→11：组合根收官顺走 4 处后的自然冻结，改 `DEFAULT_MAX_STATIC_CALLS` 一行（注释同步）；`--enforce` 实仓 11/11 绿。
5. 诚实局限：inventory 取当前树 + diff 内新增声明（历史 rename 致旧提交轻微欠计数）；多 commit 区间下单文件净增为区间累计（帽按单 PR 校准，区间用为近似）；扇出只计 Presentation/tests/tools 跨层。

## Alternatives considered

- **单文件净增 >100 即红**：n=15 时产品代码最大 73、测试 136，100 误拦正常提交。落败（150 作报告线）。
- **只做扇出闸、不做净增闸**：n=100 下扇出非零仅 27 批，只做扇出等于闸对大 PR 失声。落败（四帽并行）。
- **扇出 token 字面法**：n=100 实测 0/100（global using 使字面恒缺席）。落败（改短名 inventory 法，R1 同源）。
- **层内同计**：`ConfigSectionJson` 类同层调用在 range 演示中误报 504 级扇出。落败（同层剔除，只计跨层）。
- **--enforce 一步到位**：n=100 下 gross>600 约 10%、单文件>150 约 15% 历史 OVER，全系正常 FULL 批；当 error 太烫。落败（report 跑一批再转正）。
- **cookbook 不提额硬压**：执行半记账约 25 词，主档余 11 词；复述已压两轮，再压伤判别。落败（2804→2820，+16，理由记 manifest）。

## Consequences

- 第②闸三接线（pre-commit `--staged` / pre-push / CI `--since`）均 `--enforce`：OVER 拦路，提示拆批或在 ADR 记理由；单文件帽只量既有文件（转正经过见 [increment-gate-enforce](2026-10-14-increment-gate-enforce.md)）。
- R1b 回到零余量：新增静态调用即红；再降走迁移（进 DI/端口）。
- cookbook 主档 2820（+16）：执行半记账落 `[调试]` 耦合条目续写；冻结 archive 不动。

## Testing

- `verify-increment-discipline.py --self-test` 5/5；活树全零 OK；`--since HEAD~100` 打 OVER 行（gross 16727/单文件 +460/扇出 359/包 13）且 report exit 0；同区间 `--enforce` exit 1（红路径可达）。
- R1b `--enforce` 11/11 绿；`verify-shell-standards.sh` OK（含 hook 改动）；actionlint 本地缺装、CI 覆盖（workflow 改动靠真 runner）。
- 机器门禁：adr-format / cookbook / doc-budgets / md-links / readme-badges / handoff-structure / governance / code-health / compose-root / registration-discipline / code-conventions 全绿；review-tier FULL 待三审收口后持 Review 行过关。
- dotnet 行为面零触碰（纯脚本/hook/workflow/文档）：`dotnet build/test` 沿用 C-2 前绿（1042/1042），本批不重跑全量（改动面未达 src/tests，见 change-scope）。

## Related

- 前序：[coupling-shift-left-gates](2026-10-01-coupling-shift-left-gates.md)（三闸之母，R1b 帽源头）与 [d003-empty-catch-analyzer](2026-10-12-d003-empty-catch-analyzer.md)（同日 gates 批，包引用扇出 13 唯一样本）。
- 方法：上游 `benchmark-next-package-dependency`（扇出对标，经 2026-10-01 gh 实查）；门禁先例：[review-tier-escape-proofing](../process/2026-09-03-review-tier-escape-proofing.md)（FULL 证据行）。
