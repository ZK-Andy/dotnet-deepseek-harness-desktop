# Agent Note: 增量闸默认模式纳入未跟踪文件（读数不再偏低）

Status: implemented

Review: FULL/2026-10-15#6/R1=ok R2=ok R3=ok

Related: 闸本体 [increment-gate-enforce](2026-10-14-increment-gate-enforce.md)（`--staged`/`--since` 两个时刻的语义不变）；同族口径修正 [increment-gate-metric-corrections](2026-10-14-increment-gate-metric-corrections.md)；分布 [coupling-increment-baseline](2026-10-12-coupling-increment-baseline.md)。

## Problem

闸的默认时刻（不带 `--staged`/`--since`，即「工作树 vs HEAD」）用 `git diff HEAD` 取数，而 `git diff` **看不见未跟踪文件**——整批改动尚未 `git add` 时，默认读数只覆盖已跟踪文件的改动。2026-10-15 组合根 step-1 批次实测：默认读数 gross 438，真实规模 1831（该批 4 个提交、满批未暂存）。两个强制接线（pre-commit `--staged`、pre-push/CI `--since`）不受影响，但默认模式是**人判规模的手持读数**：偏低会把「该拆批」的批次看成合规，货真价实的 OVER 只在提交后才现形。

## Decision

1. **默认模式补入未跟踪文件**：`worktree` 时刻在 `git diff HEAD` 之外追加 `git ls-files --others --exclude-standard` 列出的文件——每个文件的整文件行数计入 gross；`.cs` 文件内容作为 added 分节参与扇出，`.csproj`/`.props` 作为 added 行参与新包计数。`--exclude-standard` 保证 `.gitignore` 面（`.plan/`、`.review-briefs/` 等本地工作档）不参与。
2. **另两个时刻不动**：`--staged` 经索引、`--since BASE` 经提交，都已携带新文件；重复计入反而破坏「同一批在两时刻读数一致」的对照。
3. **file-max 不受影响**：未跟踪文件与 `git diff` 的新增文件同为「BASE 不存在」的整文件新增，按既有口径（[increment-gate-enforce](2026-10-14-increment-gate-enforce.md)）只由既有文件喂养；gross 已经兜住它的体积。
4. **二进制按 git 语义跳过**：只在 git 的嗅探窗口（前 8000 字节，`buffer_is_binary`）内出现 NUL 的文件不计数（对应 `--numstat` 的 `- -`；窗口之后才出现 NUL 的文件仍按文本计行）；无尾换行的末行仍计一行。帽值不动（600/150/15/1）。
5. **落点**：`scripts/verify-increment-discipline.py`（`_untracked_paths`/`_line_count`/`_untracked_additions` + `_measure` 的 `worktree` 分支 + docstring）；未跟踪文件的列目录按 `--full-name` 取**仓库相对键**（同层前缀判定与扇出/清单匹配都吃这个形态），读取路径再与仓库根拼接（调用点换目录也不漏）；每个文件一次 bytes 读盘（行数与 UTF-8 解码共用同一缓冲）；`--self-test` 新增三条夹具（含嗅探窗口边界与未跟踪同层文件不误判扇出）。

## Alternatives considered

- **只在 docstring 加警告（落败）**：警告不改变读数，误判照旧发生；且「默认模式 = 工作树真实改动」是能自洽的语义，没必要留一个人为缩水的口径。
- **默认模式改为 `git add -N` 后取数（落败）**：`-N`（intent-to-add）会写索引——闸是只读检查，不能为了读数污染暂存面，用户随后的提交内容会被悄悄改变。
- **默认模式直接退化成 `--staged`（落败）**：未暂存的改动会被全部漏掉，比现在偏得更离谱；且把两个语义合成一个，丢掉「暂存 vs 工作树」这一可用对照。
- **未跟踪文件也计入 file-max（落败）**：与既有「新增文件不受 file-max 约束」的口径冲突（新文件不可拆不可裁，其体量由 gross 兜），会把一个整文件新增读成「单文件增长超帽」。

## Consequences

- 买到的：默认读数与真实批次规模对齐（同批 438 → 1831），判拆批/判合规不必先暂存；三个时刻的语义各自自洽（索引 / 提交 / 工作树 + 未跟踪）。
- 付出的：默认模式多一次 `git ls-files` 加逐个未跟踪文件读盘（本地小成本）；未跟踪的**大体积非源码物**（如临时产物、未忽略的日志）会进入 gross——这恰是真实提交会发生的事，故视为正确读数而非误报。
- 边界：被 `--exclude-standard` 忽略的文件永不参与（本地工作档面不变）；未跟踪文件的扇出按文件内容计入，与它将来提交后的读数一致。

## Testing

- `python3 scripts/verify-increment-discipline.py --self-test`：既有 11 夹具 + 新增三条（共 14 条）—— ①整文件行数（无尾换行计满行；含 NUL 回 None；**NUL 落在 8000 字节嗅探窗口之后仍按文本计行**，4010 行夹具钉住窗口边界）；②临时 git 仓库端到端：同一工作树按 `worktree` 读 910、按 `staged` 读 880，且 worktree 的 file-max 仍只认既有文件（+80）；③同层判定：未跟踪的 Infrastructure `.cs` 分节按仓库相对键（绝对键会滑过同层前缀检查，被误判成跨层扇出）。全绿。
- 实盘对照（本批自身，2026-10-15 当批未暂存时）：默认模式 gross 136 ＝ 已跟踪改动 100（`git diff HEAD --numstat` 合计）+ 未跟踪新篇 36 行；`.plan/` 等本地工作档经 `--exclude-standard` 仍在读数外（`git check-ignore` 命中 `.gitignore:4`）。
- 零触碰 src/tests：`dotnet build/test` 不受影响。
