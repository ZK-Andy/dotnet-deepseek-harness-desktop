# Agent Note: 三条挂账批的增量闸一次性豁免

Status: implemented
Review: FULL/2026-10-22/R1=ok R2=ok R3=ok

增量闸豁免：gross ≤ 800，fanout ≤ 15（实测 gross 727、fanout 3，读数见 `## Problem` 与 `## Testing`；本篇合入基线后不再属任何 `--since <base>` 的变更集，豁免随之失效——合并前本分支的后续推送仍在此帽下）。

## Problem

这一批按 [shell-router-rehoming](../architecture/2026-10-21-shell-router-rehoming.md):47 的三条挂账顺序落地：① 壳级命令注册按域下沉（[per-domain-registration-files](../architecture/2026-10-22-per-domain-registration-files.md)），②③ 域文件在册闸泛化并双向校验（[domain-file-inventory-gate](2026-10-22-domain-file-inventory-gate.md)），第三批是装配面术语退役（[assembly-surface-terminology](2026-10-22-assembly-surface-terminology.md)）。三批一次交付，实测增量 gross 727、file-max +145（`scripts/verify-compose-root.py`）、fanout 3、packages 0——gross 越默认帽 600（n=100 的 p90 线），其余三帽不越。

## Decision

按 `scripts/verify-increment-discipline.py --exempt` 做一次性豁免：`gross ≤ 800`；`fanout` 沿用默认 15、不抬帽（实测 3）；`file-max` 与 `packages` 两帽不放宽（实测 +145 / 0，均在帽内）。`pre-commit`、`pre-push`、`ci.yml` 三处豁免指针指向本篇。

## Alternatives considered

- **拆成 A / B / C 三次推送**（落败）：行数不是障碍——实测 gross 731 按路径拆成 {A+B} 486、{C} 275，两段都在 600 帽内。不可拆的是三处耦合：②③ 的判据与数据必须同提交，C8 泛化删掉了脚本里的 `BOOTSTRAP_DOMAIN_FILES`、清单改由 `scripts/domain-files.manifest.json` 供数，只提交任一半则要么引用不存在的清单、要么带着已删常量，而两者都在 `gate-criteria` 的 FULL 触发面内；C 批重写的正是 B 批新写的 C8 段 docstring 与门禁输出文案，先 B 后 C 会让 B 的 ADR 记录一批注定被改写的文案；四篇 implemented 笔记互有 6 条 `## Related` 跨目录链接，单独提交任一篇都会让 `verify-md-links.py` 报「target is not tracked by git」，而该闸是 push 前置。此外每次拆波都要单独一轮三路评审，才能让 `verify-review-tier.py` 见到新产出的 `Review:` 行。
- **压到 600 以内**（落败）：可压的只有三篇 ADR 的论据与 C8 自测夹具。夹具已从十一段展开压成表驱动骨架（`_c8()`/三态循环，脚本净增 187 → 145 行），再压就是删断言；ADR 论据是评审要求的证据面。
- **提默认帽或加路径白名单**（落败）：帽值是校准结论，单批超帽不构成改帽证据；白名单会长期放宽某类文件，与「一次性、随批次自动失效」的豁免语义不同。
- **把 C 批的术语退役另立一篇 ADR 后再推**（落败）：C 批改的正是 B 批新写的文案与本批 ADR 引用的门禁输出，分开写会产出「描述旧词」的新文档。

## Consequences

- 收益：三批一次落地，评审证据（三路 `Review:` 行）与 ADR 面保持完整；豁免随合入基线失效，不留长期放宽。
- 代价：本批 diff 比 p90 高约 22%，评审面靠已完成的 FULL 三审覆盖；下一批若再超帽，须把三处指针改指向当年的豁免篇（不命中即不生效）。
- 边界：放宽项只有 gross；`file-max`（+145，帽 150）与 `fanout`（3，帽 15）未放宽，脚本自身的净增已压到帽线以下。

## Testing

- `python3 scripts/verify-increment-discipline.py --staged --enforce --exempt <本篇>`：实跑 `gross: 727 (cap 800) | file-max(existing): +145 scripts/verify-compose-root.py (cap +150) | fanout: 3 (cap 15) | packages: 0 (cap 1)`，四帽零 OVER。
- `python3 scripts/verify-compose-root.py --self-test`、`--enforce`（`C8 inventory: 49 files in 5 domain dirs`）、`scripts/verify-code-health.py --enforce`、`scripts/verify-code-conventions.py --enforce` 全绿；`dotnet build` 0 警告 0 错误，`dotnet test` 1093/1093。
- 其余门禁与三批的实测读数见三篇 ADR 的 `## Consequences`。

## Related

- [increment-gate-enforce](2026-10-14-increment-gate-enforce.md)——帽值与「只量既有文件」的口径。
- [audit-batch3-increment-exemption](2026-10-21-audit-batch3-increment-exemption.md)——本豁免的格式与指针先例。
- [new-gates-wiring](../testing/2026-10-20-new-gates-wiring.md)——`--exempt` 的一次性语义与接线。
