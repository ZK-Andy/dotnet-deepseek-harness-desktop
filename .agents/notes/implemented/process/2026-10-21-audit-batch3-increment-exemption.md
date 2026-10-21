# Agent Note: 审计残留批 3 的增量闸一次性豁免

Status: implemented

Review: FULL/2026-10-21/R1=ok R2=ok R3=ok

增量闸豁免：gross ≤ 820，fanout ≤ 15（实测 gross 800、fanout 0，读数见 `## Problem` 与 `## Consequences`；批次落地后本篇进入基线，豁免自动失效）。

## Problem

审计残留批 3 一次落六项（自更新 dev 口径、CI 三腿全量、macOS 应用图标、三平台布局断言、两个打包腿脚本自测、组合根 C7 静态工厂通道与死项核对），实测增量 gross 800（三轮评审修复后的终值；含本篇与三处指针），上限留一次复审修复余量，默认帽 600（n=100 的 p90 线）。六项横跨 `.github/workflows/ci.yml`、五个 `scripts/**`、三篇 ADR 与五份文档，彼此独立；批次的 FULL 三审已完成一轮并进入修复后复审。

## Decision

按 `scripts/verify-increment-discipline.py --exempt` 做一次性豁免：`gross ≤ 820`；`fanout` 沿用默认 15、不抬帽，实测 0。`pre-commit`、`pre-push`、`ci.yml` 三处豁免指针指向本篇；批次落地后本篇进入基线，指针不再命中，豁免自动失效，后续批次仍受 600 帽约束。

## Alternatives considered

- **拆成两波（交付面 / 组合根闸）**：落败——两半都要重跑 R1/R2/R3 并各自重写 ADR 与文档指针，而内容已过一轮全量三审；拆波换来的是评审成本翻倍，不是更小的评审面。
- **削到 600**：落败——可削的只有自测夹具与 ADR 论据；已把 `scripts/dev-sign.sh` 自测从桩调用逐条展开压成共享 `_run`/`_calls_has` 骨架（185→169 行），再削就是删断言或删论据。
- **提默认帽**：落败——帽值是 n=100 的 p90 校准结论，单批超帽不构成改帽证据（与 `coupling-increment-enforce` 的「帽值不动」一致）。
- **按路径白名单**：落败——白名单会长期放宽某类文件，豁免要看「批次是否在变更集内」，两者语义不同且前者不可自动失效。

## Consequences

- 收益：批次一次落地，评审证据与 ADR 面保持完整；豁免自动过期，不留长期放宽。
- 代价：本批 diff 比 p90 高约 16%，评审面靠已完成的 FULL 三审覆盖；指针更新（三处一行改动）在下一批需再次指向当年豁免篇或保持原状（不命中即不生效）。
- 边界：`file-max`（+139，帽 150）与 `fanout`（0，帽 15）两帽未放宽——放宽项只有 gross（实测 800，帽 820）。

## Testing

- `python3 scripts/verify-increment-discipline.py --staged --enforce --exempt <本篇>`：实跑 `gross: 800 (cap 820) | file-max(existing): +139 scripts/verify-compose-root.py(cap +150) | fanout: 0 (cap 15) | packages: 0 (cap 1)`，四帽零 OVER。
- 其余门禁与脚本自测见批 3 的三篇 ADR（`ci-full-suite-legs`、`delivery-surface-self-verification`、`compose-root-construction-channels`）。

## Related

- [increment-gate-enforce](2026-10-14-increment-gate-enforce.md)——帽值与「只量既有文件」口径。
- [new-gates-wiring](../testing/2026-10-20-new-gates-wiring.md)——`--exempt` 的一次性语义与接线先例。
