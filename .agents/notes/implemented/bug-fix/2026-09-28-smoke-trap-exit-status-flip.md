# Agent Note: 冒烟腿静默翻红（共享库 EXIT trap 翻转退出码 + 临时路径漏回收）

Status: implemented

Review: FULL/2026-09-28#6/R1=ok R2=ok R3=ok
Review: LIGHT/2026-09-28#7/R2=ok

## 评审处置（FULL，R1/R2/R3 三路；首轮 + 第 2/3 轮验轮）

### 首轮

- **R1/R2/R3 一致 B1：`common.sh` 注释把不变量记到不存在的门禁**——原写「由 S7 用行为探针钉住（`verify-shell-standards.sh`）」，而该门禁只有 S1–S6，且本 ADR 的 Alternatives D 正把 S7 记为落败备选（同一提交内三处互相矛盾）。**已修**：注释改指共用夹具的 `trap-*` 三条断言（契约的事实家仍为 `docs/script-standards.md`）。
- **R3 B2：C 批 ADR 的 Testing 段留旧值**——该段写 `docs/script-standards.md` 302/500，而本批正改了该文件。**已修**：按冻结 index 实测回写 330/500。
- **R3 B3：本 ADR 的 Problem 症状强度高于所引 run**——原写「6 腿签名高度一致 + `rc_total=0` + 约 11ms」，实测只有第 3 轮两腿有 `rc_total` 行（该行由第 3 轮才加入），6 腿区间为 11.1–17.1ms。**已修**：按逐轮证据改写，第 1/2 轮的翻码点标【推断 · 未证】。
- **R3 B4：C 批 ADR 内「断言总数」三个值**（Decision 四 217/46、Consequences「现 204 条」、Testing 226/53）。**已修**：现行计数只留 Testing 一处，其余两处改为指回该处并显式限定快照时点与统计域。
- **R1 S1（采纳）**：句柄里 `&& -d "$root"` 无成本可删（`rm -rf` 对缺失路径本就返 0，实测）；`-n "$root"` 与 `${…:?}` 经实测保留（后者去掉会让模板退化为 `/XXXXXX`）。
- **R1 S2（采纳）**：`common.sh` 头部注释里的登记表/空行翻码复盘裁掉，只留两条机制约束与 trap 纪律。
- **R1 S3（采纳）**：夹具注释改为契约式（三侧同锁 + 为何不能省失败侧），事故叙述移出。
- **R1 S4 前半 / R2 S1（采纳）**：回收断言原先只证「根没了」，对「路径逃出根」的坏实现全绿。**首轮改法**：探针改按生产形态 `x="$(…)"` 建路径并打印 `ROOT=/F=/D=`，断言加「`F`/`D` 以 `ROOT/` 开头」。
- **R1 S4 后半（不修）**：夹具里 `lib_dir` 是否改为复用 `_SMOKE_LIB_DIR`——不修：夹具隐式依赖兄弟库的私有全局不值（首轮 R1 自判「保留现写法」），且该写法与仓内 per-file 定位先例一致。
- **R3 S4（采纳）**：cookbook 条目两处机制错误——「脚本**之外**的 trap」改为「脚本自己的 trap」（翻码的正是 source `common.sh` 注册的那个）、删掉「输出被重定向吃掉」的错误归因、删掉退役登记表叙述。
- **R3 S5（采纳）**：`docs/script-standards.md` 只留契约句，登记表 rationale 移出（规范正文不写 rationale）。
- **R2 S2（采纳，收窄措辞）**：原称漏调 `common_tmp_trap` 时 `${…:?}`「不再静默」——实测仅在调用方带 `-e` 时中止（`set -uo pipefail` 的**赋值形态**下打印错误后继续、`rc=0`；裸调用形态则中止）。**已修**：Decision 二按实测收窄。
- **R3 S1/S2/S3/S6（采纳）**：环境对照标【探索性】（n=1）；`verify-shell-standards.sh` 行数改实测 247；Alternative C 的兼容性理由改写为「按变量名写回受动态作用域遮蔽」；删 head `Related:`（唯一关系移入尾节）。
- **R3 S7（属性说明）**：`Review:` 行由收口时写入，属评审记录，不在被审对象内。
- R1 建议「合并成功侧与回收侧断言」**驳回**：两条分开才能在「只翻码不漏盘」形态下定位病灶（实测该形态仅 `trap-keeps-ok` 红）；合并只会红一条、读不出是翻码还是漏盘。

### 验轮（第 2 轮）

- **R1 B1 / R2 B1：首轮的修法自己丢了一条判据轴**——把回收断言从「根不存在」换成「路径以 `ROOT/` 开头 + 路径不存在」时，**丢掉「根自身被删」**（`rm -rf "$root"/*` 形态全绿）；且 `ROOT/*` 是 glob、`*` 跨 `/`，`ROOT/../esc` 与「根内符号链接指向 `/tmp`」两种形态也能过（R2 实测）。**本轮改法**：改 `-ef` 比目录 inode 判根内 + 断言补回 `! -e "$probe_root"`。
- **R2 S1（采纳）**：Decision 二的 `-e` 措辞按调用形态限定（赋值形态 vs 裸调用），见上。
- **R3 B1（采纳）**：C 批 ADR 的 302 换成 333 后仍不对——333 是**上一冻结 tree** 的值；正确值是冻结 index 实测的 **330/500**，两处 ADR 已统一为 330。
- **R3 S1（采纳）**：正文里内联的审核条目码（「（R2 评审核）」等）删去——评审报告不入库，条目码在 durable 正文里无落点；该信息由本节承载。
- **R3 S2（采纳）**：首轮把 R1 S2/S3 与 R3 S4/S5 并成一句的条目已拆为四条，逐条给「现象 → 处置 → 证据」。
- **R3 S3（采纳）**：原口径「机制叙述只留本 ADR」与实况不符。**按实况定口径**：事故复盘（现场细节、翻码机制与坏实现矩阵）只在**本 ADR**；契约在 `docs/script-standards.md`、判别序在 `docs/cookbook.md`、句柄纪律在 `scripts/lib/common.sh` 头部**各留一句 why**，属正当形态。
- **R3 S4（采纳）**：cookbook 条目再精简，释放预算余量。
- **R1 S2（第 2 轮新提，驳回）**：建议删 `smoke-selftest.sh` 里一处既有空操作行以留尺寸余量——该行在增量之外（`OUT`/`LOG` 的赋值有后续消费点），本批不动增量外的行。

### 验轮（第 3 轮）

- **R2 B1：Testing 矩阵有一格取值错**——「根内符号链接逃出」行的 `trap-keeps-ok` 曾记 `FAIL`，那是构造差异（符号链接目标尚不存在 → `mktemp` 失败 → 探针中止）的产物，不是逃逸被成功侧拦住。**已修**：按规范构造记 `ok`，并注明「探针中止时成功侧亦红」。
- **R1 S1 / R2 S2：`-ef` 判据过严**——它等价于「父目录 inode == 根 inode」（只认直属子项），而契约写的是「路径建在根内」；契约合规的**嵌套**布局（根内子目录）会被误判红（R1/R2 各自实测）。**已修**：改为 `cd -P` / `pwd -P` 规范化后比前缀，嵌套布局与相对路径的根都放行（见 Testing 的放行侧）。
- **R2 S1：恢复「根必须绝对路径」的守卫** → 不需要：规范化后 `ROOT` 恒为绝对路径，前缀判据自带该约束。
- **R1 S2：失败消息跨行**——`tfail "…（probe=$probe）"` 把 4 行探针输出插进单行消息。**已修**：`tr '\n' ' '` 压成单行。
- **R3 B1：处置段漏记一条结论**——首轮 R1 S4 后半（`lib_dir` 不修）未被任何条目承载。**已修**：补入首轮段。
- **R3 S1/S2/S3/S4/S5（采纳）**：首轮条目曾把最终修法回填成首轮做法（与本段其余条目矛盾）→ 改为逐轮记各自改法；「改述为…」的措辞与实际编辑不符 → 改为按实况定口径；Testing 正文里的评审轮次叙述 → 压成当前态陈述；「六种坏实现」→ 限定统计域；第 2 轮新提的驳回项 → 归入第 2 轮段。

### 处置闭合核验（三路回执）

三路各自只对第 3 轮结论做回执核验（不重扫全批）：**R1 = 0 Blocker / 0 Suggestion**（并额外探了「根名+后缀」的前缀共享兄弟路径等误绿方向，无洞）；**R2 = 0 / 0**（并核实规范化判据连「目录本身是指向根外的符号链接」这一旧盲区也判红）；**R3 首回执 = 1 Blocker / 1 Suggestion**——B：`:32` 声明「已改为实际动作」而实际未改（无事实支撑的闭合声明），S：Testing 未标「判据定版后重跑」。两条按 R3 给的一行修法处置后回执 **0 / 0**。合计三路三轮 + 回执共 **12 处 Blocker 报告**，全部落在本段。

## Problem

C 批（`39c860a`）之后 linux 冒烟双腿三连全红（`36363806570` / `36365626333` / `36366396555`，共 6 条腿、n=3 轮）。共同签名：腿自身的判据全绿（结论行与 PASS 证据尾行照常打印），**step 却报 `exit code 1`，日志里没有任何 error 行**。逐腿量「末行输出 → step 报错」的间隔：**11.1–17.1ms**（6 腿；最小 11.1ms = 第 3 轮 amd64），即脚本收尾之后还有一步在翻退出码。

`rc_total` 汇总行只在第 3 轮两腿出现——该行由第 3 轮的 `cf2a255` 才加进脚本，故**汇总行的缺席不是另一种机制**，只是当时还没有这行取证。第 1/2 轮的翻码点属【推断 · 未证】：由下述本地复现（该句柄形态恒翻码）与第 3 轮的实见共同支持，但未在那两轮日志里直接看到。

三轮排障依次排除了下游各面（均非本缺陷）：

- **不是环境漂移**：绿轮 `36357116884`（`c93bd4c3`，红轮前 1 小时 54 分）的显示栈安装输出与红轮逐行同款——两者都是 `xvfb 2:21.1.12-1ubuntu1.6 → 1ubuntu1.8`、`2 upgraded, 36 newly installed`。"runner 把 xvfb 就地升级"在绿轮上同样发生。（对照轮 n=1，【探索性】。）
- **不是 wrapper**：`package.yml` 的冒烟命令（`dbus-run-session -- xvfb-run -a -s …`）本批未改动；`dbus-run-session` 的 POSIX 实现原样透传子进程退出码（daemon 退出只打印、不参与返回），`xvfb-run` 的 `clean_up` 失败会先打印一行——**那一行在日志里不存在**。
- **不是腿逻辑**：本批零 `src` 变更，腿自身判据全部通过。

机制（本地实测，可复现）：`set -e` 下 **EXIT trap 的末命令失败会把脚本的 `exit 0` 翻成 exit 1**（bash 5.3：`set -e; trap false EXIT; exit 0` → rc=1；去掉 `set -e` → rc=0）。C 批把 deb 腿的临时文件回收从「函数内直接 `rm -rf`」改成 `common_tmp_trap` + 登记表 + trap 逐条 rm，于是有两个同源缺陷：

1. **登记从未生效**：登记表 `_common_tmp_paths` 是调用方 shell 的变量，而调用面是 `log="$(common_tmp_file)"`——`$(…)` 在 subshell 里跑，赋值传不回来。登记表恒空 → 临时路径**一个都没回收**（`smoke_deb` 里原来的回收点已被删除）。
2. **空登记串翻码**：空登记串经 `printf '%s\n'` 仍产出一个**空行**；空行上 `[[ -n "$p" ]] && rm -rf -- "$p"` 的 AND-list 判假返回 1，`set -e` 就地收走管道 subshell，句柄在 `return 0` 之前就以 1 返回，trap 随即把脚本退出码从 0 翻成 1——**且失败输出为零**。

两个缺陷互为因果：登记失效让登记串为空，空登记串又让 trap 翻码。故**腿全绿也会红**，且红了不给任何线索。

## Decision

一、临时文件纪律改为**单一临时根**（`scripts/lib/common.sh`）：`common_tmp_trap` 建根（`mktemp -d`），`common_tmp_dir` / `common_tmp_file` 建的路径都落在根**里面**，trap 删根即全清。根路径是普通 shell 变量，subshell 照常继承，所以「`$(…)` 里建路径」不再丢回收；调用面（`x="$(common_tmp_dir)"`）一字不动。登记表与逐条 rm 一并删除——出问题的机制整体消失，而不是给病灶打补丁。

二、句柄契约成文（[docs/script-standards.md](../../../../docs/script-standards.md) 临时文件纪律）：**EXIT trap 句柄不得改调用方退出码**——句柄须以 0 返回、体内每一步都不得中断（管道、`A && B`、裸 `rm` 都在禁列）；回收失败只留痕（`rm -rf … || warn … || true`）不抛。`common_tmp_dir/file` 要求在 `common_tmp_trap` 之后调用：漏调即 `${…:?}` 报错退出——**带 `-e` 的调用方**随之中止（仓内唯一调用面 `smoke-install-linux.sh` 即此形）；`-e` 缺位时，赋值形态会打印错误后继续，裸调用形态则中止。故该守卫的职责是「点名调用序」，不是兜底全责。

三、回归锁进**共用冒烟夹具**（`scripts/lib/smoke-selftest.sh` 的 `_st_trap_probe` + 三条断言，三平台自测各 +3）：成功侧不翻红、**路径真在根内且根退出后不留**（探针在路径仍存活时用 `cd -P`/`pwd -P` 规范化，判「规范化后以根为前缀」；父侧再断根源已不存在）、失败侧不吞红。

## Alternatives considered

- **A. 只给 `_common_tmp_cleanup` 里的 `rm` 加 `|| true`**（照 `af0d307` 的 win 修法）。落败：只治翻码、不治「登记传不出来」——临时路径照漏；且空行 AND-list 仍会翻，得给体内每条命令都挂 `|| true`，判据脆弱。
- **B. 保留登记表，把 `&& rm` 改成 `if` / `|| continue` 的逐行 rm**（本缺陷首版修法）。落败：翻码治好了、回收仍空——实测 `x="$(common_tmp_dir)"` 之后临时根照旧留在 `/tmp`（`LEAK: /tmp/tmp.Oo456Oykcp remains`）。等于把「静默翻红」换成「静默漏盘」，红得更隐蔽。
- **C. 改 API 为 `common_tmp_dir <变量名>`**（`printf -v` 写回调用方，绕开 subshell）。落败：治本但要改三个调用点 + 文档契约；且「按变量名写回」把调用方的变量命名拖进库的契约面——`printf -v` 写的是动态作用域里最近的同名变量，调用方一旦 `local` 遮蔽就写错地方。临时根方案同样治本且不动调用面。
- **D. 在 `verify-shell-standards.sh` 加一条 S7 行为探针**（子 bash 里装句柄、断言退出码不变）。落败：S2 尺寸闸对门禁自身生效，而该文件实测 247/250 行——要么给门禁开 `allow-long` 豁免（门禁给自己开后门），要么同批拆门禁（本批不该顺手重构门禁）。本仓对「门禁查不到的接线缺陷」的既定处置是**注释 + 共用夹具**（C 批 `smoke_resolve_windows` 窗口契约同款），故 S7 改落夹具。

## Consequences

- 收益：linux 腿的 step 退出码重新由**腿结论**决定（trap 不再有翻码权）；临时路径**真回收**（C 批起一直在漏，`/tmp` 里每次冒烟留一个 DSH home）；`common.sh` 的临时文件面比原先更短（无管道、无登记表）。
- 代价：临时路径形状由 `/tmp/tmp.XXXX` 变为 `/tmp/tmp.XXXX/XXXXXX`（多一层，日志里的路径随之变化）；`common_tmp_dir/file` 的前置调用顺序成为硬契约（漏调即 `:?` 报错）。
- 未覆盖：**入口脚本自写 trap**（mac 卸载挂载点、win 停进程、rpm 容器腿拷日志）的同类翻码不在夹具面——它们各自带 `|| true` 或显式回收，夹具无法在不构造真环境的前提下探针，这一面仍归 S1–S6 与评审。
- 未覆盖：`xvfb-run` 自身的 `clean_up`（`xauth remove` / `kill $XVFBPID` 失败也会静默 exit 1，因其 `ERRORFILE` 缺省 `/dev/null`）不在本仓可修面；本轮已按源码逐行排除，但它仍是**本仓之外**的潜在翻码点（复现「无输出的红」时按 cookbook 该条先查 trap 层）。

## Testing

- **回归锁与坏实现矩阵（本地；判据定版后重跑）**：三平台冒烟自测 `56 / 55 / 53` 条断言全绿（含新增三条）。在 `/tmp` 的 `scripts/` 副本上改副本 `common.sh` 后跑副本的 `smoke-install-linux.sh --self-test`（下表七行 = 六个坏实现 + C 批原版基线）：

  | 坏实现 | `trap-keeps-ok` | `trap-reclaims-tmp` | `trap-keeps-fail` |
  |---|---|---|---|
  | C 批原版（登记空 + 空行翻码） | FAIL | FAIL | ok |
  | 句柄翻码但回收照做 | FAIL | ok | ok |
  | 句柄吞掉退出码 1 | ok | ok | FAIL |
  | 临时路径建到 `$TMPDIR`（根外） | ok | FAIL | ok |
  | 临时路径经 `根/../` 逃出 | ok | FAIL | ok |
  | 临时路径经「根内符号链接」逃出 | ok | FAIL | ok |
  | 清空根内路径但留下根目录 | ok | FAIL | ok |

- **放行侧（契约合规的实现不得误红）**：根内**嵌套**子目录布局（`根/sub/XXXXXX`，删根即全清）与**相对路径**的根（`mktemp -d -p .`）均 `ok / ok / ok`、rc=0——判据是「规范化后在根内 ∧ 根退出后不存在」，不认实现把路径建在根下第几层。
- **判据取舍**：三条断言各锁一个方向、无恒真空断言；`trap-keeps-fail` 对**本批实际缺陷**不判红（翻码恰好也是 1），它是拦「恒 0」方向的锁——故成功侧那条不可省。`trap-reclaims-tmp` 的两条判据缺一不可：只看「根没了」会漏「路径建到根外」，只看「路径不存在」会漏「清空根内但留下根目录」（探针中止时成功侧也会红，属 fail-loud，不计为逃逸被拦）。
- **行为面实测（本地逐条）**：`set -euo pipefail` 下 ①`exit 0` + 建临时路径 → rc=0 且根内文件与根全删；②`exit 1` → rc=1（真失败不被吞）；③未建任何临时路径 → rc=0；④`rm` 真失败（子目录 0500）→ rc=0 且留一行 warn；⑤漏调 `common_tmp_trap` → 报错 rc=1。
- **门禁**：`verify-shell-standards.sh`（S1-S6，26 文件）、`verify-cookbook.py`、`verify-doc-budgets.py`、`verify-md-links.py`、`verify-adr-format.py`、`shellcheck -S warning`、`actionlint` 全绿。
- **dispatch 实跑（`36371907647`，commit `bbb8271`）**：`package.yml` **五腿全 success**——linux amd64/arm64 **首次转绿**（此前三连 ❌：`36363806570` / `36365626333` / `36366396555`）、mac 双 rid ✅✅、win ✅；同轮 `ci.yml` push 轮 `36371903414` success（含 `docs` job 的三平台自测）。双腿日志以 `== [linux] 腿汇总：rc_total=0` 收尾且**无** `Process completed with exit code` 行——即本缺陷的真环境判据已取得：腿的 step 退出码不再被 trap 翻转。
- **计数口径（本地 vs CI）**：本地（装了 ImageMagick）三平台自测 `56 / 55 / 53`；CI `docs` job 实测 `52 / 49 / 49`（该 job 不装 imagemagick）。差值两处来源：`scripts/lib/smoke-selftest-verdict.sh:27` 的「无 convert 即 `skip` 截图内容见证夹具」分支（三平台各 −4），以及 mac 专属的 `scripts/smoke-install-macos.sh:68` 同类分支（mac 再 −2）。判据的判红侧在本地取证（上表），CI 侧只跑得到不依赖 convert 的那部分。

## Related

- [script-layer-consolidation](../process/2026-09-28-script-layer-consolidation.md)：本缺陷的引入批次（其三平台 dispatch 的 linux 面即本条的现场）。
- [shell-settle-behavior-gate](2026-09-27-shell-settle-behavior-gate.md)：冒烟落定门语义的来源，本批未动其语义。
- [docs/script-standards.md](../../../../docs/script-standards.md)：临时文件纪律与 trap 句柄契约的成文家。
- [docs/cookbook.md](../../../../docs/cookbook.md)「腿全绿、step 静默红」：同类静默翻红的判别序（先钉脚本自身退出码，再查 trap 层）。
