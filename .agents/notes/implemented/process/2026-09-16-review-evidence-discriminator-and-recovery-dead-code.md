# Agent Note: 评审证据行的同日判别符 + 恢复页过渡屏死链退役

Status: implemented

Review: FULL/2026-09-16#2/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`scripts/verify-review-tier.py` 的新鲜度判据是「`Review:` 行是本批新增行」。该判据只看行文本是否落在 `git diff -U0` 的 `+` 行集合里，于是同日第二个 FULL 批不必真的重跑评审，只要让既有证据行重新成为一条新增行即可顶包。两条实测先例：

- **空白扰动**：证据行按 `R1=ok\s+R2=ok\s+R3=ok` 匹配，`\s+` 接受任意空格。`3230a5d` 把 `R1=ok R2=ok R3=ok` 写成 `R1=ok  R2=ok R3=ok`（多一个空格）；`9853d5a` 把空格从 `R1=ok` 之后搬到 `R3=ok` 之前。两批的证据都落在不可见字节上。
- **表头搬位**：`b61901b` 把证据行从「中文（双语暂不启用…）」行之上整行搬到其下，文本逐字未变，git 仍报「删一行、加一行」，该行照样算本批新产。

另一件同批处置的清理：`RecoveryPageBuilder.BuildRestartingScript(bool)` → `RestartingSkeleton` → `UiCopy.RestartingNote` 整链零调用方、零测试。唯一调用点（`Program.cs` 里的 `EvaluateJavaScriptAsync(Services.RecoveryPageBuilder.BuildRestartingScript())`）随 `cfa4113`（2026-08-29「参照对齐批次一」）的 `restartTriggered`/恢复覆写路径退役一并删除（见 [reference-alignment](../../implemented/architecture/2026-08-29-reference-alignment.md) 的对应条），该链自那时起无调用方。

## Decision

一批收口两件，同一动机：把靠人记的判别与靠人记的死码都变成机器可判。

### 1. 证据行规范化 + 同日序号（`scripts/verify-review-tier.py`）

- `REVIEW_LINE_RE` 收成规范形 `^Review: FULL/<date>(#N)?/R1=ok R2=ok R3=ok$`：R 令牌之间只认**单个空格**，`R1=ok  R2=ok` 这类令牌间扰动不再匹配。行首/行尾空格与 `\r` 结尾仍被 `ln.strip()` 归一而匹配——它们由下一条的 base 逐字比对拦下（新文件无 base 行可比，但那里候选行本就真是新的）；两条规则合起来闭合字节扰动通道。
- 日期后接受可选同日序号 `#N`（正整数；`#0` 与前导零不认），作为同日第二个 FULL 批的**可见判别符**：`Review: FULL/2026-09-16#2/R1=ok R2=ok R3=ok`。
- 新增「base 版本逐字已有即非新产」：候选证据行即使落在本批新增行集合里，只要它在**整篇笔记**的 base 版本中逐字已有，就不算本批产出——表头搬位、以及把正文行或 60 行窗口外的同文本行搬进头区，都由这一条关闭。该判定在 `_added_lines_for` 成功之后才读 base 版本，读不到即「base 无此文件」（新建或改名目的地），不是「diff 时刻不可判定」（后者仍由上游 fail loud 承担）。

### 2. 恢复页过渡屏死链退役

删 `RecoveryPageBuilder.BuildRestartingScript`、`RecoveryPageBuilder.RestartingSkeleton`、`UiCopy.RestartingNote` 及其分段注释——纯减法；`BuildScript`（崩溃恢复页活跃路径，5 个调用点）不动。

## Alternatives considered

- **只收紧空格、不加 `#N`**：落败——同日第二个 FULL 批若复用**同一篇** ADR 承载证据就无法产出新行（日期相同、空格规范、文本逐字相同），只剩「另立 ADR」一条路；`#N` 让同一篇笔记也能承接同日后续批的合法证据。
- **只加 `#N`、不收紧空格**：落败——判别符可见了，旧的空白捷径仍在，等于给规范写法而不关后门。
- **base 比对做空白归一化（正则仍严格）**：落败——它与匹配正交、不会放行扰动行，但除不掉 Consequences 那段具名假红（那段源于严格正则拒绝双空格行，候选行到不了 base 比对），反而会把「就地规范化该行」的补救判成非新产；净代价是把「逐字相等」降为空白等价类，两侧判据不再共用同一个可读契约，收益为负。
- **要求证据 ADR 必须本批新建（`git status` 的 `A`）**：落败——同日折叠（proposed 直写 implemented）与给既有 ADR 补新证据行两种正当形态都会被误拦；「新增行 + base 无逐字同文本」覆盖这些形态且语义更贴「本批新产」。
- **把 `#N` 做成必填**：落败——既有 implemented ADR 的规范行都是无序号形态，必填会把仓库既有证据全判非法；序号只在同日第二批需要区分时出现。
- **`BuildRestartingScript` 改为接线（恢复过渡屏）**：落败——那是新增行为面（何时展示、与接力就绪/看门狗如何互斥），需要独立的产品理由与 ADR；本批是清理，按「零调用方即删」处置，将来真需要再按新行为立项。
- **只更新待办、不动死码**：落败——该链已被 R1 报出一次；留一个公共方法会让每次动恢复页都要重新判一遍「死码还是漏接线」，与「机器可查的不变量必须接进门禁」同向的处置是删掉。

## Consequences

- 收益：同日多个 FULL 批各自需要**可见**的判别符（`#N`）而非不可见字节；字节扰动、表头搬位、把正文行或 60 行窗口外的同文本行搬进头区三种绕过路径都关闭——base 比对取整篇笔记而非头区投影。先例 `3230a5d`/`9853d5a`、`b61901b` 与投影形态各由夹具钉死。恢复页公共面少一个无调用方方法。
- 代价：给既有 implemented ADR 追加证据行时，若该笔记 base 版本已有逐字相同的行，必须改用 `#N`，否则门禁拦下——有意为之的强制。
- 代价：base 比对取整篇笔记，因此一篇笔记若在 base 版本的**正文**里逐字引用了与自己头区相同的证据行（把真实 `Review:` 行当示例抄进正文），它后续合法写入的同文本头区行会被判非新产。按 fail-closed 拦下：改用 `#N` 或让示例不逐字等同即可。全仓现状 39 条规范证据行全部位于头区，无此形态。
- 残留（具名覆盖缺口）：`2026-09-12-coverage-baseline-from-ci-cobertura.md` 现存一行双空格证据行（`9853d5a` 写下）。该行不匹配规范形，因而不构成证据。后果是一段**起点早于 `9853d5a`、终点在其之后，且范围内没有新产规范证据行**的 `--since` 范围才会判「FULL 缺证据」假红（例如以更早提交为 base 的 PR 式 `pull_request.base.sha`；终点批次自身携带新产规范证据行时该范围有合法证据，不假红）——本仓直推 main，pre-push 的 `origin/main` 与 push 事件的 `event.before` 都随 `9853d5a` 前移，故当前无命中路径。处置留给一个不携带 FULL 证据的独立批次做规范化：放在本批会把该行变成「本批新增行」而替本批顶包，正是本条判据要关的形态。
- 代价：`src` 侧零行为变更，测试面不新增用例（删的是零调用方代码）；门禁面由 `--self-test` 夹具承担。

## Testing

- `python3 scripts/verify-review-tier.py --self-test`：24 例夹具全绿——相对基线新增六例：「空白扰动的证据行不算证据」/「`FULL/<date>#2/...` 是合法证据」/「同日第二批复用同一 ADR 时 `#N` 构成新产」/「`#0` 不是合法序号」/「表头搬位的逐字同行不算新产」/「正文行搬进头区不算新产」。
- 后两例经变异实测确认非空转：base 比对换回头区投影 → 仅「正文行搬进头区」变红；删掉 base 比对块 → 它与「表头搬位」一起变红（证明候选行确在 `_added_lines_for` 的 `added` 集合内，旧判据会放行）。
- 历史先例复核：`3230a5d`（`R1=ok` 之后加空格）与 `9853d5a`（空格搬到 `R3=ok` 之前）追加的证据行均为双空格形态，规范形下不匹配；`b61901b` 的搬位行文本逐字等于 base 版本。三条都由实测复算。
- `dotnet build` 0 error、`dotnet test` 全绿；`verify-ui-copy` / `verify-code-health --enforce` / `verify-code-conventions --enforce` / `verify-adr-format` / `verify-md-links` / `verify-doc-budgets --manifest` 全绿。

## Related

- [review-evidence-freshness-gate](2026-09-13-review-evidence-freshness-gate.md)：本判据的 owning ADR，其 Decision 与 Consequences 按本批同步。
- [review-tier-escape-proofing](2026-09-03-review-tier-escape-proofing.md)：档位机械化；本篇收的是「证据新鲜度」面的同类漏洞。
- [review-scope-narrowing](2026-08-31-review-scope-narrowing.md)：简报门禁复用本脚本的档位判定（`_classify` 不受本批影响）。
