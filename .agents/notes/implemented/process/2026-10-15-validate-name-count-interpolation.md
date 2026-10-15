# Agent Note: _validate_name 段数报错插值修复（补 f 前缀）

Status: implemented

Review: FULL/2026-10-15/R1=ok R2=ok R3=ok

## Problem

`scripts/verify-adr-format.py` 的 `_validate_name()` 在路径段数非 3 时的报错，第二段字符串缺 `f` 前缀，真触发时原样打出 `(3 segments; got {len(parts)})` 而非具体数字。判红逻辑不受影响（照样判红），只是报错文案缺数。2026-10-15 R2 评审旁见，未立项；按“小瑕疵当场修、禁止挂账”纪律本批直接修。

## Decision

补 `f` 前缀使段数插值；`_self-test` 加一条插值断言：错位夹具（2 段路径）的报错须含 `got 2`，防回退。既有 A–H 夹具与校验逻辑不动。

## Alternatives considered

- **只补 f 不加断言（落败）**：单字符回归无网可兜，一行断言成本为零，采纳加断言。
- **继续挂账等顺手（落败）**：上次挂账两周无人碰，用户已明令禁止此类挂账。

## Consequences

- 买到的：段数报错带真实数字；回归有网。付出的：无（`_self-test` 多一次临时树扫描，毫秒级）。

## Testing

- `python3 scripts/verify-adr-format.py --self-test` 通过（含新增插值断言）。
- `python3 scripts/verify-adr-format.py` 全量绿；同批四门禁绿；`verify-review-tier --staged` 定档后走 R1/R2/R3 三重审核。
