# Agent Note: 增量闸两处口径修正（扇出清单只纳 Infrastructure 声明；新包按 id 计）

Status: implemented

Review: FULL/2026-10-14/R1=ok R2=ok R3=ok

Related: 闸本体 [increment-gate-enforce](2026-10-14-increment-gate-enforce.md)（本篇更正其第 2 条「扇出/新包两帽口径不变」）；分布与前身 [coupling-increment-baseline](2026-10-12-coupling-increment-baseline.md)。

## Problem

2026-10-14 的恢复面波 2（原生失败面承载）实盘预检暴露两处**假阳性**，二者都让「新增依赖」这一正当形态在 `--enforce` 下无路可走：

1. **扇出清单被本 diff 自己声明的类型污染**：`_infra_inventory` 把「diff 内新增的任意类型声明」并入清单，不限 Infrastructure 文件。于是一个批次每落一个新类型（Core 端口、Presentation 适配器），它的每一处新调用点都计成「跨层扇出」——实测该批 44 命中里 **34 命中是「新类型 + 它自己的测试」**（`UiCopy` 14、`DialogToolAvailability` 8、`RecoveryDialogText` 7、`RynFailureDialog` 4、测试内嵌类型 1），真正的跨层引用只有 10（`HostLog` 9 + 新 Infrastructure 类型 1）。【探索性：n=1，设置＝恢复面波 2 的暂存集 `--staged` 预检】
2. **新包帽按行计，而 CPM 下一个依赖两行**：仓库用中央包管理（`Directory.Packages.props` 是版本单一事实源），加一个依赖需 `PackageVersion` + `PackageReference` 两行，帽却是 1。即按行口径「加第一个依赖」恒 OVER——【推断：CPM 行数的算术后果，未实测；n=100 校准窗口内「新增包引用 1/100」那一批并未 OVER，窗口内未出现两行形态。】该帽对任何新依赖都等于禁入，出路只剩改闸或绕闸（`--no-verify`）。

## Decision

1. **扇出清单只纳 Infrastructure 声明**：`_infra_inventory` 改收 `(path, added_lines)` 分节，只把路径位于 `--infra-src` 之下的新增声明并入清单；Core/Presentation/tests 的新类型不入清单。帽值 15 不动（口径修正后同批预检 10）。
2. **新包按 id 计**：`_packages` 只认 `PackageReference`/`PackageVersion` 的**开标签**（闭合标签不匹配），取 `Include=`/`Update=` 的包 id 去重计数；无 id 的开标签行各计一次（不静默丢弃）。帽值 1 不动（`Ryn.Plugins.Dialog` 一条依赖 = 1）。
3. **帽值一律不动**：600/150/15/1 沿用 n=100 校准；本篇只改两帽的**度量对象**，不改阈值。净效果是削掉上述两类假阳性，真阳面（新增跨层基础设施调用、新增依赖）照旧计。
4. **落点**：`scripts/verify-increment-discipline.py`（两函数 + docstring）与其 `--self-test` 新增三条夹具（另将「in-diff 声明」一条按新口径改写）。

## Alternatives considered

- **把扇出帽提到 44+（落败）**：那是把报警器调低——44 里 34 是假阳性，提帽会连真实的跨层增长一起放行；且 n=100 的 max=76 说明真阳形态本就在帽附近。
- **把新包帽提到 2（落败）**：CPM 下 2 行 = 1 个依赖，提帽等于允许一批加两个依赖；被行数污染的语义该改语义而不是改阈值。
- **把 `Directory.Packages.props` 的行排除在计数外（落败）**：会漏计「只改版本」的 `Update` 行，并把「哪一行算数」写死在路径上，语义仍不对准「几个依赖」。
- **维持现状、让批次拆到不触发（落败）**：本批的 44/2 与拆批无关——任何「新类型 + 新依赖」的批次都命中；症状在闸不在批。
- **为绕开闸而不引入依赖（自研三平台原生框）（落败）**：绕开度量不等于修正度量；自研 P/Invoke 或子进程分支的代价远高于一条 Ryn 依赖，且把上游已维护的平台分支搬进本仓。

## Consequences

- 买到的：两个帽重新对准它们声称度量的对象（跨层基础设施引用、新依赖数）；新增类型/新增依赖的正当批次不再被结构性拦下。
- 付出的：无 `Include` 的 `PackageReference` 写法按行计（保守）；被删类型/被删依赖不计（与既有口径一致——只看 added 行）。
- 已知边界（沿用前篇）：扇出仍按短名匹配（同名类型可误配）；类型改名并同时改引用时命中仍落在 added 行上照计。
- 取代关系：前篇 [increment-gate-enforce](2026-10-14-increment-gate-enforce.md) 第 2 条的「扇出/新包两帽口径不变」不再成立（该篇已记 Erratum）。

## Testing

- `python3 scripts/verify-increment-discipline.py --self-test`：既有 8 夹具 + 新增三条（CPM 两行计一、多 id 各计一且无 id 行仍计一、多行元素含子节点计一（闭合标签不计））＝ 11 条；原「in-diff 声明」一条按新口径改写（清单只纳 Infrastructure 声明）。全绿。
- 首轮评审（R1/R2，2026-10-14）0 Blocker + 3 Suggestion（闭合标签双计、`cs_plus` 死变量、`new package refs` 文案与新口径不同名）——三条已采纳并折入（夹具与文案随之上移一条）。
- 假阳性实测【探索性：n=1，设置＝恢复面波 2 暂存集预检，2026-10-14】：扇出 44 → 修正后 10（帽 15）；新包 2 行 → 1 id（帽 1）；gross/file-max 两帽不受影响（该批 709 走拆波，不靠本篇）。
- 首轮评审（R3，ADR/文档面，2026-10-14）0 Blocker + 3 Suggestion——裁定：`Review:` 行随收口为实（R1/R2/R3 均首轮 ok，不预留，采纳）；夹具计数按实际改为「既有 8 + 新增 3 ＝ 11」（采纳）；两处数值补强度标注（44/34 分解标【探索性】+ 设置、CPM 两行恒 OVER 标推断）（采纳）。驳回可选面：`AGENTS.md` 的帽标签与 cookbook 条目均不陈述度量对象、帽值未变，不随改。
- 零触碰 src/tests：`dotnet build/test/format` 沿用波 1 绿（1037/1037、覆盖率 63.88%，见 `scripts/test-baseline.json`）。
