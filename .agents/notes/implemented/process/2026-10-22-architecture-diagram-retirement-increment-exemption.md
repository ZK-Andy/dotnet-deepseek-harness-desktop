# Agent Note: 架构图退役批的增量闸一次性豁免

Status: implemented
Review: FULL/2026-10-22/R1=ok R2=ok R3=ok

增量闸豁免：gross ≤ 15000，fanout ≤ 15（实测 gross 14896、fanout 0、file-max +4；读数见 `## Problem` 与 `## Testing`；本篇合入基线后不再属任何 `--since <base>` 的变更集，豁免随之失效）。

## Problem

[架构图退役](../simplification/2026-10-22-retire-architecture-diagram.md)删除 `docs/architecture.html`（13735 行 `deliver` 产物）、`assets/architecture.svg`（1030 行）与图源 JSON，diff 面 gross 14896 行，越默认帽 600（n=100 的 p90 线）。净增为负、fanout 0、file-max +4——超帽全部来自删除生成物，不是新增代码。

## Decision

按 `scripts/verify-increment-discipline.py --exempt` 做一次性豁免：`gross ≤ 15000`；`fanout` 沿用默认 15、不抬帽（实测 0）；`file-max`（+4，帽 150）与 `packages`（0，帽 1）两帽不放宽。`pre-commit`、`pre-push`、`ci.yml` 三处豁免指针指向本篇。

## Alternatives considered

- **拆提交或拆 PR**（落败）：删除面是同一份生成物，拆不开；拆后任一提交仍带着 13735 行的删除。
- **把 HTML 压行后再删**（落败）：该文件是 `deliver` 输出，压行等于手改生成物，与「产物不手改」纪律冲突。
- **提高默认帽**（落败）：单批删除生成物不构成改帽证据；帽值是校准结论，长期放宽与「一次性豁免」语义不同。
- **让门禁对纯删除豁免**（落败）：会让「先加后删」的往返也免检，闸的语义被稀释；一次性豁免只覆盖本批。

## Consequences

- 收益：删除面一次落地，生成物的体量不撑破 gross 帽。
- 代价：豁免随本批基线失效；三处指针从 [three-deferred-items-increment-exemption](2026-10-22-three-deferred-items-increment-exemption.md) 转指本篇后，旧篇的 gross ≤ 800 豁免不再生效（其批次已入基线，无需再用）。
- 边界：放宽项只有 gross。

## Testing

`python3 scripts/verify-increment-discipline.py --staged --enforce --exempt .agents/notes/implemented/process/2026-10-22-architecture-diagram-retirement-increment-exemption.md` → gross 14896 ≤ 15000、fanout 0 ≤ 15、file-max +4 ≤ 150、packages 0 ≤ 1，exit 0；去掉 `--exempt` 同命令 exit 1（豁免是它生效的唯一原因）。

## Related

- [retire-architecture-diagram](../simplification/2026-10-22-retire-architecture-diagram.md)
