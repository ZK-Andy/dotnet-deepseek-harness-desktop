# Agent Note: verify-adr-format 错位文件判红（消灭假绿）

Status: implemented

Review: FULL/2026-10-15/R1=ok R2=ok R3=ok

## Problem

`scripts/verify-adr-format.py` 的 `_scan()` 对首段不在三 lifecycle（`proposed/implemented/rejected`）的 `.md` 直接 `continue`：不计数、不报错。错位落盘的新 ADR（如 lifecycle 目录外、层数不对）照样 `Checked 212 OK` 全绿，错位文件根本没被查——R3 在 2026-10-15 评审中挖出这处假绿。

## Decision

非豁免 `.md` 错位即判红而非跳过：豁免项（`archived/` 内、`*.zh.md`、顶层 `README.md`）继续跳过；其余首段非三 lifecycle 的文件记一条 `FAIL`（路径须为 `<lifecycle>/<class>/<name>.md`），不计入 `checked`。`_self-test` 新增两夹具：notes 根直放文件、lifecycle 拼错目录，均断言 exit 1。命名/骨架校验逻辑不动。

## Alternatives considered

- **保持跳过只告警（落败）**：错位仍是绿，门禁失去拦截意义；WARN 易被忽略。
- **把错位文件计入 checked 再判红（落败）**：`Checked N` 口径混入非笔记文件，历史数字不可比；不计数只报错更干净。
- **顺手收紧 class/命名校验（落败）**：越界改动，本批只修假绿一处；其余收紧记 TODO 不顺手做。

## Consequences

- 买到的：错位落盘即红，`Checked N` 只含真笔记；自测覆盖错位形态。付出的：今后 notes 根误放草稿即红，草稿须放 `.plan/` 或正确 lifecycle 目录。
- 豁免面不变：`archived/`、`.zh.md`、顶层 `README.md` 仍跳过。

## Testing

- `python3 scripts/verify-adr-format.py --self-test` 通过（含新增错位两夹具）。
- `python3 scripts/verify-adr-format.py` 全量绿（`Checked` 数与修前一致，错位夹具只存在于临时目录）。
- 同批四门禁（cookbook/doc-budgets/md-links）绿；`verify-review-tier --staged` 定档（预期 FULL）后走 R1/R2/R3 三重审核。
