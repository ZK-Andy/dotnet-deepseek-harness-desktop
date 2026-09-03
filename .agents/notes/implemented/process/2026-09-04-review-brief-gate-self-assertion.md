# Agent Note: review-brief-gate-self-assertion（评审简报门禁自证——"已盖"必须真跑过）

Status: implemented

Review: FULL/2026-09-04/R1=ok R2=ok R3=ok

## Problem

2026-09-04 自更新安装包清扫（self-update-prune-consumed-packages）FULL 三审暴露简报信任缺口：主会话简报把 `src/.../StalePackagePruner.cs`、`tests/...` 等文件列为「陪跑文件（机器门禁已盖，扫读确认即可）」，但**主会话实际漏跑了 `dotnet format --verify-no-changes`**（只跑了 code-health/conventions）——R2 评审代理为核实「已绿」声明，实测 format 发现 exit 2（FINALNEWLINE + IDE0005，CI 必红），为拿真实退出码还因管道吞码重跑两次，成为该路耗时大头（审核约 15 文件精读 + 分钟级 format 反复）。

根因不是 R2 太慢，而是**简报的「机器门禁已盖」是主会话口头声明、无自证机制**——评审代理无法区分「已绿」与「我以为绿」，只能实测核证，把主会话该付的门禁成本转嫁给每路评审代理。声明失真时，陪跑文件的「只扫读不精读」前提崩塌（代理发现要精读的门禁其实会红）。

## Decision

**简报「陪跑文件（机器门禁已盖）」必须携带门禁自证**，`scripts/verify-review-brief.py` 机械校验：

1. **简报模板 Scope 区加「门禁自证」行**（位于陪跑文件行之后）：
   `- 门禁自证（主会话实跑）：<脚本>:<exit>，…`（如 `- 门禁自证：dotnet-format:0，dotnet-test:0，code-health:0`）
2. **机械校验规则**（verify-review-brief.py 增两条）：
   - 简报含「陪跑文件」行（声明机器门禁已盖）→ **必须含「门禁自证」行**（缺失即违规：声明已盖却无自证 = 未证）；
   - 门禁自证行逐项校验 exit 码**全部为 0**（任一非 0 = 声言已绿但门禁实际红 → 违规拦截，不允许把红的门禁文件列陪跑）。
3. **规范同步**：review-scope-narrowing §4.1 字段规则补「门禁自证」条款；feature-flow 步骤 5 提门禁自证为简报前置。

**边界**：门禁自证只覆盖「主会话实跑过且绿的机器门禁」——dotnet build/test/format、verify-*.py、change-scope.sh 等。**ADR↔代码一致性、语义判读类永远不属"已盖"**（评审代理仍需核）；门禁清单外的面（如新 ADR 格式）若列陪跑仍需自证对应门禁（adr-format）。

实现细节（评审收口补强）：零值校验限定到「门禁自证」行文本（`_selfassert_text` 提取），不扫整份简报——正文任意 `<token>:1`/`:2` 片段（如文件:行引用）不会误报；自证单项正则容忍全角冒号（`[:：]`——中文语境书写易混）；违规文案附格式样例。

## Alternatives considered

- **只加文字条款「简报门禁声明必须真跑」**：review-tier-escape-proofing 教训——纯文字补丁拦不住执行者自觉（本次漏跑正是主会话没把 format 当门禁）。落败；必须机械校验。
- **评审代理一律不信门禁声明、全量实测**（本次 R2 实际行为）：最稳但把主会话成本转嫁给每路代理、分钟级 format 每路重跑，FULL 三审耗时 ×3。落败；主会话自证一次，代理信任有据。
- **简报删掉「已盖」表述、陪跑文件改深审**：把机器已盖文件升格为精读对象，直接消灭信任面——但每路都精读全部变更文件 = 回到无界铺开，与简报定界初衷冲突。落败；保留陪跑/深审区分，补自证。
- **自证 exit 允许非 0（报告式）**：非 0 的「已盖」声明矛盾——红门禁的文件不能算已盖、不得列陪跑。落败；非 0 即违规。

## Risks

- 主会话填自证时「虚报 0」（没跑写 0）：无法机器防——自证是诚实声明而非门禁重跑；缓解：自证值语义 = 主会话对评审代理的信用承诺，虚报会在代理实测时暴露（本次正是实测暴露），声誉机制约束 + 定向检查项可点「核某门禁自证」。
- 门禁清单膨胀：每加一个 verify-*.py 都要想是否列入自证；缓解：自证只要求「列了陪跑文件的机器门禁」，未列陪跑的门禁无需自证。

## Consequences

- **行为**：`verify-review-brief.py` 增「门禁自证」规则（陪跑声明已盖 ⇒ 必含自证行、exit 全 0），self-test 从 6 扩到 8 fixtures；本批三份简报回填真实门禁自证（主会话实跑 exit 全 0）。
- **机制效果**：主会话把门禁列陪跑前必须先自己实跑到位——漏跑（如同批初版漏 format）会被机械校验拦下，不再转嫁给评审代理实测，FULL 三审无需每路分钟级重跑门禁。
- **边界**：自证只覆盖机器门禁；ADR↔代码一致性、语义判读类永远不属「已盖」，评审代理仍需核（简报保留此收窄口径）。
- **测试**：`verify-review-brief.py --self-test` 8/8、`--enforce` 通过；本批门禁（format/test/code-health/conventions/adr-format/md-links）全绿。

## Related

- [review-tier-escape-proofing](2026-09-03-review-tier-escape-proofing.md)（implemented）：评审档机械判定的母 ADR（verify-review-tier.py）；本机制同属「补丁若只加文字条款必然重演」的教训链——门禁自证用机械校验防主会话漏跑。
- [review-scope-narrowing](2026-08-31-review-scope-narrowing.md)（implemented）：评审简报模板与字段规则的规范本尊；本机制在 §4.1 字段规则增「门禁自证」条款。
- [feature-flow](../../../workflows/feature-flow.md)（process）：步骤 5 的评审执行契约；本机制同步「简报门禁自证」句。
- [self-update-prune-consumed-packages](../architecture/2026-09-04-self-update-prune-consumed-packages.md)（implemented，同批）：触发本机制的实证——2026-09-04 prune 批 FULL 三审暴露「主会话漏跑 format 却写已盖」，是本机制立项的直接动机。
