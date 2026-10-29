# Agent Note: 根 AGENTS.md 去重：门禁清单与预算表交还单一事实源

Status: implemented

## Problem

根 `AGENTS.md` 是每个 agent 每轮都读的常驻面，但正文里两块内容在复述别处已有的事实，并且已经开始与事实源冲突：

- **门禁清单**：23 条 shell 命令的 fenced block。`docs/testing.md` §门禁 用表格 + 「hooks 快检查 / CI 穷尽矩阵」分档矩阵描述同一批闸，且是超集——`verify-host-command-ids`、`verify-core-purity`、`package-version.sh`、`release-preflight.sh`、`release-notes.sh`、`coverage-summary.py`、`dev-sign.sh`、`smoke-linux-rpm.sh` 只在 testing.md 出现；反向只有 `verify-registration-discipline`、`verify-increment-discipline` 两条独存于 AGENTS.md（grep testing.md 命中 0）。漂移已发生：被删清单里 `verify-compose-root` 一条写「C4/C6 随旧编排器退役」，testing.md 的对应行写「排序器形状（C6）」。
- **字数预算表**：11 行表格，同一节自注「唯一实现在 `scripts/doc-budgets.manifest.json`（本表为其视图）」。提交 `1b92ffb` 就是为对齐这张表与 manifest 的漂移。
- **评审检查项**：D001/D002/D003 的细节与紧随其后的「已机器化进 build 门禁，评审不再兜底」重复。

测量：`wc -w AGENTS.md` = 632 词，其中被删的三块合计约 295 词（46.7%）。预算闸对前两块失明——`verify-doc-budgets.py` 的 `count_words` 排除 fenced code 与表格（口径由该脚本自测里「代码块不计词」「表格不计词」两条夹具固定），故门禁清单与预算表从不进入计数；同一口径下改动前为 685 词、改动后 705 词（口语化指针句计入、代码块不计入），文件原始体积 632→349 词。

## Decision

- `## 质量门` 只留指针：门禁清单、每道闸判据、hooks/CI 分档的唯一事实源是 `docs/testing.md`；push 前最小证据集按 `dsh-pre-push-checks` 技能选取。
- `## 字数预算` 只留指针：上限清单与判据的唯一实现在 `scripts/doc-budgets.manifest.json`（AGENTS.md 自身 ≤800 词亦在其中），改额走 manifest 并附 `_justify_bump`。
- 交还前先补齐事实源：`docs/testing.md` 的门禁表与分档矩阵各加两行，登记 `verify-registration-discipline.py`（闸档位取自 `.githooks/pre-commit:55-56`、`ci.yml:144`）与 `verify-increment-discipline.py`（`.githooks/pre-commit:60-61`、`pre-push:65-66`、`ci.yml:113,186`）。
- `docs/testing.md` 的 `verify-doc-budgets.py` 行改指 manifest 为「上限清单与判据」的唯一实现，删去它原先对 AGENTS.md 表格的反向依赖。
- 评审检查项删去 D001–D003 细节，留一句已机器化进 build 门禁（VSTHRD200 / VSTHRD100 / DSH0001）；R1/R3/IPC 三条不动。

## Alternatives considered

- **只删 fenced 清单、保留预算表**：删代码块按体积收益最大，但保留的表格仍会与 manifest 漂移，`1b92ffb` 的对齐成本还会再付。表格与 manifest 只能留一个，选 manifest——它是判据实现，不是视图。
- **反向合并：把 testing.md 的表搬进 AGENTS.md**：常驻面免跳转，看似省一步；但要把 8 道 AGENTS.md 没有的闸连同 hooks/CI 分档矩阵一起搬进来，常驻面反而更大。
- **让预算闸把代码块与表格计词**：能治失明，但 shell 命令行会按词吃掉预算，各文档上限失去可比性；先治重复，口径另议。
- **不动结构、只同步两处措辞**：漂移可暂时消除，但每加一道闸仍要改两处，复发结构不变。

## Consequences

- 收益：AGENTS.md 原始体积 632→349 词；门禁事实只剩一处，加闸只改 testing.md 表与矩阵。
- 代价：常驻面不再有命令速查，agent 要知道有哪些闸需跳一次 `docs/testing.md`；文档字数上限也从常驻面移到 manifest。
- 残留：`verify-doc-budgets.py` 仍不计代码块与表格，AGENTS.md 若再堆命令或表格，预算闸不会报警——该缺口本次未修。
