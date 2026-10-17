# Agent Note: md-links-tracked-targets

Status: implemented
Review: FULL/2026-10-18#5/R1=ok R2=ok R3=ok

## Problem

`verify-md-links.py` 只判「目标在本机是否存在」，不判「目标是否入库」。作者机器上 `HANDOFF-todos.md`、`.plan/`、`.evolve/` 都在，所以指向它们的相对链接本地全绿；CI 全新检出没有这些文件，docs job 直接红——`39079397610` 就是这么红的：一篇新 ADR 把 gitignore 的 `HANDOFF-todos.md` 写成相对链接，本地门禁全过，CI 报 `missing target`。这类「本地绿、CI 红」不是偶发：任何本地专有文档都可能被链接，而门禁没有任何机制发现。

## Decision

- 链接目标必须属于**入库集合**（`git -C <root> ls-files`）。目标存在于磁盘但未入库 → 判红，错误行写明「克隆后是死链」。
- 只校验**入库文档**：本地专有笔记（`HANDOFF*.md`、`.evolve/` 等）自身之间的链接不判——它们不进克隆，不构成本门禁要守的读者面。
- 入库集合读不到（不在 git 工作树）→ **fail loud**，不静默跳过这条规则；`--self-test` 用合成的入库集，不依赖 git。

## Alternatives considered

- **未入库目标直接跳过（放行）**：落败——committed 文档对任何克隆者仍是死链，本地绿只是把问题推迟到 CI；门禁的价值正是让本地与 CI 判据一致。
- **把 `HANDOFF*` / `.plan/` / `.evolve/` 逐个加进排除面**：落败——治不了任意本地专有文件，且排除面只增不减，正是这套门禁最该避免的形态。
- **门禁前先 `git clean` 或临时克隆再跑**：落败——成本高、靠人记得做；门禁的判据应当自带，不依赖运行环境的干净程度。
- **要求所有文档都不许有本地链接（靠评审）**：落败——可机器判定的不变量必须进可执行门禁（AGENTS.md 编码约定），评审兜不住。

## Consequences

- 本地与 CI 的链接判据一致：原先只在本地专有文档里解析得到的链接不再计入，目标数因此下降；门禁输出同时打印「目标数 / 扫了几篇入库文档 / 跳过几篇本地文档」，扫描面与跳过面可直接复核（计数随文档增减，不在此抄写）。
- `.plan/` 内的文档仍不被扫描（扫描面排除不变），但**入库文档链到 `.plan/` 会判红**——这正是要的语义。
- 目标解析到仓库之外同样判红：仓外文件在任何克隆里都不存在，与「目标必须入库」是同一条不变量。
- 已知边界（判据细节与失败形态的唯一家是脚本 docstring 的「Tracking rule」与「Boundary」两段）。

## Testing

- `--self-test` 四类夹具全绿，且经变异测试确认非空：删掉排除判据 → 红（`bin/gen.md`、`.plan/note.md` 在合成入库集里，只靠排除规则保持安静）；删掉「只校验入库文档」的跳过 → 红（未入库的 `ignored-note.md` 里有死链）。
- 夹具覆盖：缺失目标、死锚点、未入库目标、仓外目标；`checked/scanned/skipped` 三元计数一并断言；`--include-skills` 分支仍覆盖。
- 全仓实跑全绿（目标数、扫描篇数、跳过篇数见门禁输出，不在此抄写）；正向验证——把 `../HANDOFF-todos.md` 临时加回 `docs/cookbook.md`，门禁立刻报「is not tracked by git」，撤回后恢复绿。

## Related

- [handoff-split-governance](2026-08-31-handoff-split-governance.md)——`.plan/` 排除段成文于此（Decision 6），本篇的扫描面以它为准。
- [doc-adr-layer-cleanup](2026-09-28-doc-adr-layer-cleanup.md)——`archived/` 不再整目录跳过、纯链接修复成为冻结例外；本篇的「只校验入库文档」与之叠加。
- [gate-common-shared-module](2026-09-29-gate-common-shared-module.md)——链接/锚点模型的家（`LINK_RE`/`heading_slugs`），本篇不改那层。
