# Agent Note: 脚本层收口（共享库唯一家 + 脚本规范门禁 + 三包/三烟去重）

Status: implemented

Review: FULL/2026-09-28#5/R1=ok R2=ok R3=ok

评审：R1（简化面）/R2（代码面）/R3（ADR·文档面）三路结论与逐条处置见下文「评审处置」段（评审收口后回写）。

## 评审处置（FULL#5，R1/R2/R3 三路首轮）

三路首轮共报 **11 条 Blocker**，全部由我独立复现后修复；修复**未开验轮**（用户收尾指令，与本仓 [review-round-convergence](2026-09-28-review-round-convergence.md) 批同例），故本行 `R*=ok` 取「本轮 0 未闭合 Blocker + 建议已书面裁定」义。

### Blocker（逐条：现象 → 修复 → 复现证据）

- **R1 B1 / R2 B2（同一处）：`packaging-common.sh` 用 `die`/`warn` 却从不 source `common.sh`**——所有 fail-loud 路径退化成 `die: 未找到命令`（rc=127，诊断文本丢失；`package-windows.sh` 的 `warn` 分支还会在 `set -e` 下中断打包）。**已修**：库自带 `source common.sh`（与 `smoke-verdict-lib.sh` 同形）；并把 `packaging_self_test` 的两条 fail-loud 夹具从「只看 rc≠0」改为**断言诊断文本**——该加固经证伪：撤掉 source 行后夹具判红（`诊断文本缺「不支持 ARCH」（实得：… die: 未找到命令）`），还原即绿。
- **R1 B2 / R2 B1（同一处）：rpm 容器腿恒红**——宿主只透传 `SMOKE_WAIT_SECONDS`/`SMOKE_SETTLE_SECONDS`，而容器内脚本从不把它们映射成库读的 `SMOKE_WAIT`/`SETTLE_WAIT`（改名前 heredoc 自设 `SETTLE_WAIT`），`set -u` 下 `seq 1 "$SMOKE_WAIT"` 直接炸、整腿一跑就死。**已修**：窗口解析收进共享库 `smoke_resolve_windows`（唯一家，宿主三腿 + 容器腿同调），容器内脚本显式调用；并**新增 `smoke-linux-rpm-inner.sh --self-test` 离线端到端夹具**（假壳产出 ①+铸币+代理流量 → 断言落定/结论行/退出码 0，共 3 条），把「本机无 docker 故该腿零覆盖」的缺口关掉；`.github/workflows/ci.yml` 的夹具步已接线。
- **R1 B3 / R3 B3（同一处）：门禁自测在 `.cache/bin` 装法下必崩**——`smoke_standards_self_test` 里 `local root` 遮蔽脚本级 `ROOT`，S6 夹具分支 `[[ -x "$root/…" ]]` 在 `set -u` 下报「未绑定变量」，第 7 条夹具与汇总永不执行（CI 因 `GITHUB_PATH` 掩盖）。**已修**：删遮蔽；工具查找改用**门禁自身仓库**（`SELF_ROOT`）的 `.cache/bin` → `PATH`（与 `--root` 受检树解耦，夹具模式同源）。实测：有/无 shellcheck 的 PATH 下均 7/7 绿。
- **R2 B3：新门禁自己不成立**——该文件 251 行 > 自订 250 行闸，而 S2 的豁免判据 `grep -qE 'verify-shell-standards: allow-long .+'` 命中的是**本文件头部对该标注的说明**（以及夹具字符串），于是「提到标注」即自我豁免。**已修**：豁免判据改**行首注释锚定**（`^#[[:space:]]*verify-shell-standards: (allow-long|no-errexit) .+`），并把文件压到 247 行——门禁须先过自己这道闸。
- **R3 B2：ADR 把待跑写成已取得证据**——Testing 段以「CI 实跑（真 runner 证据）：`workflow_dispatch` 三平台打包腿」为证，而该 run 尚未发生（`origin/main` 仍是批基）。**已修**：Testing 段改为「已跑（本地）」与「待跑（推送后 dispatch，附 run 号回写）」两分，未跑即不列；本节即回写位。
- **R3 B1：内联 Inno 段行数虚高**（写「115 行」，实为 77 行；`package-windows.sh` 注释里的「~110 行」同源）。**已修**：两处改实数（77 / 模板 82）。
- **R3 B4：探针退役以改写 ADR 正文记入，违 `Erratum:` 约定**。**已修**：正文恢复原样，改在 `Status:` 下插 `Erratum: 2026-09-28 — 落点换家…正文不动`。
- **R3 B5：7 篇现役 ADR 仍把 `scripts/smoke-settle-lib.sh` 当现行落点**。**已修**：按 README 的 `Erratum:` 约定各补一行（指向拆并后的 `scripts/lib/smoke-wait-lib.sh` 与 `smoke-verdict-lib.sh`），正文一律不动。

### Suggestion（逐条裁定）

**采纳**（21 条）：三份 `smoke_self_test` 骨架收进库（三入口各留一行）／`FULL_RE`/`BOOT_RE` 判定串收进库（6 份 → 1，容器腿不再 `-e` 传串）／`SMOKE_LOG_DIR` 落盘段收成 `smoke_dump_logs`／linux PASS 路径的 5 行与 30 行尾巴改「一次 30 行」／`file_size` 收编 `build-companion-tgz.sh` 与 `release-preflight.sh` 的两处手抄／mac 的 `wait_verdict` 两侧回归锁移进共用夹具（原先只锁在平台文件里）／`wait_verdict` 门控加 `①已见`（②安装链收工不再白等满 150s 预算，R2 S3）／`verify-package-layout.sh` 用法错误改 exit 2（与规范退出码语义一致）／`list_entry_scripts` 更名并修正注释／S5 抽取放宽到缩进与 `function name` 形态／`doc-budgets.manifest.json` 恢复单行条目（只留真增量一行）／win/mac/rpm 的临时目录回收补齐／`docs/architecture.md` 打包描述跟现实（Inno 唯一链、布局断言扩面）／`package-windows.sh` 注释行数／`docs/testing.md` 补工具门两行（`install-linters`/`actionlint` 的「在哪跑」补家）／cookbook 条目补可执行抽样命令／`docs/script-standards.md` 的「一律 `common_tmp_*`」措辞放宽（自定义清理 trap 是正当形态）。
**驳回**（2 条，附理由）：①**19 组 `_st_live`/`_st_kill_live` 抽 table helper**（R1 S2）——省约 36 行，但要重写 19 条正在通过、且是本批「判据能失败」证据的夹具，风险不对称；②**4 处自测汇总骨架抽 helper**（R1 S9）——净省约 14 行，却把「显示格式」这一件事拖进三个域（common/packaging/verify）共用的库。
**部分采纳**（1 条）：R2 S1（S4 只认 `${NAME:-}` 形态，裸 `$UPPER` 调用方契约全局不在判据面）——该规则查的是**库的声明面**，而本轮缺陷在**调用方是否接线**（库头已声明），机器面查不到；故不加新判据，改以「`smoke_resolve_windows` 漏调即炸」的注释 + 窗口契约夹具 + 容器腿端到端夹具承接。

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`scripts/` 有 29 个文件、无一条脚本规范（`grep -rl '脚本规范|script-standard' docs .agents AGENTS.md` 零命中，`AGENTS.md` 只有「shell 等待 ≤30s」与「新规范优先机器化」两条沾边）。规范化缺位的代价在 2026-09-27 的三平台震荡里全部兑现——复制粘贴成了唯一复用工具：

- **三份包脚本的 ~40 行同构头部**（`--stage-only` 解析、`ARCH` 归一化、`VERSION` 默认、`PUBLISH_DIR` 推导、publish 目录断言）各写一份，且架构别名集合各不相同（linux 收 `amd64/x86_64/arm64/aarch64`，mac/win 收 `x64/amd64/x86_64/arm64`）。
- **闭包残留检测四处**（`package-linux.sh` / `package-macos.sh` / `package-windows.sh` / `verify-package-layout.sh` 各一份同逻辑断言）。
- **一等等待循环四处**：`smoke-install-linux.sh` 的 `wait_url`、mac 与 win 脚本各自的 for 循环、以及 rpm 容器腿 heredoc 里再抄一遍（容器那份连心跳/看门狗都重写）。
- **自测夹具三份互抄**：三平台 `--self-test` 共 83 处断言调用（37/31/15），大量夹具（落定/裁决/看门狗/回退门）逐字重复，且已各自漂移（同一回归锁在 linux 有、win 无）。
- **尺寸无闸**：`smoke-install-linux.sh` 503 行、`package-windows.sh` 269 行（含 77 行 Inno heredoc——`cat > … <<ISS_EOF` 到 `ISS_EOF`；抽出后模板 82 行）；零调用点的 `probe-gui-freeze.sh`（210 行）自 2026-08-28 起在库里躺了一个月。
- **旋钮失控**：没人设置、文档也未记为可覆写的旋钮无机器拦（A 批实测 `INSTALL_WAIT_SECONDS` 即此形态）。
- **最薄的两环恰好没人跑**：83 处冒烟自测断言 CI 从不调用——资产已写好、维护成本照付、收益为零。

## Decision

一、**共享库成为唯一家**（`scripts/lib/`，库文件非可执行、不自设 shell 选项）：
`common.sh`（消息模板 `log/warn/error/die`、`mktemp`+`EXIT` trap 临时文件纪律、`file_size`）、
`packaging-common.sh`（三包脚本共用头部 + `packaging_assert_layout` + 离线自测）、
`smoke-wait-lib.sh`（落定/等待/心跳/看门狗/回退门，含从平台脚本提升进来的 `smoke_wait_ready`）、
`smoke-verdict-lib.sh`（结论行/存活门/像素见证/证据打印/无人值守三元组/裁决静默等待）、
`smoke-selftest.sh`（共用夹具）。原 `smoke-settle-lib.sh` 按此拆分删除；rpm 容器腿改调共享等待实现，容器内脚本另拆为 `scripts/smoke-linux-rpm-inner.sh`（**可从 heredoc 字面块变成可 lint/可评审的普通脚本**）。

二、**脚本规范落 `docs/script-standards.md` 并机器化**：新增 `scripts/verify-shell-standards.sh`（S1 入口须 `set -euo pipefail`／S2 单文件 ≤250 行／S3 库不可执行且不自设选项／S4 旋钮声明／S5 共享库单源／S6 `shellcheck -S warning` 零告警），CI `docs` job 与 pre-commit 各跑一次；`shellcheck` 与 `actionlint` 由新增的 `scripts/install-linters.sh` **钉版本 + sha256 校验**装入（CI 与本地同版本，见 Alternatives）。D3 两项偏离按方案推荐落地：**保留 kebab-case 命名**、**尺寸闸取 250 行**（不迁语言）；action SHA 钉版不动（见未覆盖②）。

三、**三包脚本去重与去脆弱兜底**：Inno 脚本落 `packaging/windows/installer.iss.in` 模板（渲染器在 `package-windows.sh`，渲染后残留 `@占位@` 即 fail loud）；自签实现（mac `codesign` / win `signtool` + 自签证书）唯一家 `scripts/dev-sign.sh`，三包脚本只在 `SELF_SIGN=1` 时转调；hdiutil 的 `srcfolder` 回退链与 `chmod +x … || true` 静默删除（失败即 fail loud）；`verify-package-layout.sh` 的 `--platform` 改**必填**（原先按「内容根里有没有 .exe」隐式判平台，linux/mac 腿整段静默跳过）、补**主程序 + 可执行位**断言（此前只有 Windows 断主 exe，linux/mac 可静默发布「无主程序包」）、配 `--self-test`。

四、**冒烟拆分 + 自测接线**：三平台入口 + rpm 腿 + 三库 + 共用夹具，god script 尺寸 503/387/318 → 208/183/177；CI `docs` job 每次 push 跑 9 份离线夹具（本批落地时共 217 条断言：全仓共用面 46 条 + 平台专属各留几例；**现行条数见 Testing**——锁会随批次继续加）。

五、**`probe-gui-freeze.sh` 退役**：210 行、零调用点（workflow/hook/其他脚本全无引用）、7 个 `FREEZE_*` 旋钮在 CI 与文档里都无设置点、唯一价值是一次已归档的真机排查。判别知识留 cookbook（条目改写为手工抽样动作），退役事实记在[取证探针 ADR](2026-08-28-gui-freeze-forensics-probe.md)。

六、**A3 的裁定与本方案原判不同：保留 mac 的 `wait_verdict`**（连同 `SMOKE_VERDICT_SECONDS`、`echo_verdict_lines`），但迁进共享库并把「进程已死即跳过静默等待」写进调用点。方案原判是删（判为「用完即删」残留），删不得的理由：**截图是像素见证门的输入**，而应用侧 grace 重载会改写终页——撤掉这层时序对齐，绿跑的截图会更常拍到重载前旧页，**证据保真度下降**（本仓「证据诚实」是 [page-verdict-gate](../testing/2026-09-26-page-verdict-gate.md) 一路的硬约束（verdict-honesty-repair 已并入该篇））。A3 真正该修的是「无人跑 + 时序不可复现」：现由共用夹具把「增长后稳定才返」「grace 触发重置静默」两侧钉住。

七、**A4/A5 收口（含一处对方案判断的更正）**：三处 `|| echo "plugins 缺失"` 删除，但**方案称其「不可达」不成立**——`set -o pipefail` 下 `ls … 2>&1 | head -3` 的管道状态取 `ls` 的非零值，`||` 会触发（实测打印 `plugins 缺失`）：它与已被 `2>&1` 送进管道的 `ls` 错误行重复，属**冗余诊断而非死代码**。`wait_url` 提升进库后，容器腿的外层循环与宿主腿同实现（A5 记的「容器内手抄等待循环」至此真正去重）。

## Alternatives considered

- **`verify-shell-standards.sh` 写成 Python**（与 13 个 `verify-*.py` 同形）：落败——它判的是 shell 面（旋钮/库形态/尺寸）且要逐文件驱动 shellcheck，`.sh` 形态与判据面同语言；代价是 bash 做文本判定更啰嗦，用 `--self-test` 七条夹具兜住。
- **旋钮声明判据放宽为「名字在文件前 N 行出现即算声明」**：落败——代码里的**读取**本身会命中该行，判据恒真（首版自测当场证明：夹具的 `DEAD_KNOB` 被判为「已声明」）。改为只认**注释**行。
- **放宽 S1 允许无 `-e`**：落败——容器腿的 `set -uo pipefail` 有真实理由（dnf 失败要走显式分支打印诊断），但那是**豁免**不是默认；故走行内标注 `verify-shell-standards: no-errexit <理由>`，全仓仅此一处。
- **shellcheck 用 apt/`command -v`（不钉版本）**：落败——诊断集合随版本漂移，「本地绿 CI 红」会直接废掉门禁可信度；改为钉版 + sha256（摘要取自 GitHub releases API 的 asset digest 并在版本库硬编码，避免「版本换了摘要没换」只在运行时暴露）。
- **actionlint 走 docker action 或 `go install`**：落败——docker 多一层镜像供应链且本地沙箱无 docker 跑不了；`go install` 依赖 runner 的 Go 工具链。选 GitHub release 二进制 + 摘要校验，本地与 CI 同一条路（`install-linters.sh`）。
- **Inno 模板用 `sed` 渲染占位**：落败——Windows 路径含 `\`，sed 替换串要转义、`&` 也是元字符；bash 参数展开是字面替换，无此坑。
- **容器腿保留 heredoc**：落败——引号 heredoc 的内文对 shellcheck 与评审都是不透明字面块，还要额外防宿主变量串入；拆成普通脚本后两者都能读。
- **自签完全移出 `package-*.sh`**（由 workflow 单独调 `dev-sign.sh`）：落败——mac 必须在生成 dmg **之前**签（dmg 里的 `.app` 无法再签），移出后发布链路得拆成「打包—签名—封 dmg」三段；保留 `SELF_SIGN=1` 转调既让实现单一化又不改链路形态。
- **删掉 mac 的 `wait_verdict`**（方案 A3 原判）：见 Decision 六。
- **三份平台自测各留一份**（现状）：落败——83 处断言里大半重复且已漂移；改为「共用面一份 + 平台专属各留几例」，共用面新增双源腿夹具（`wait-dual-source`）等原先哪份都没覆盖的形态。

## Consequences

- 收益：跨脚本逻辑有了唯一家，新增平台/新腿只需接共享库——「一次改动 N 处同步」的耦合面消失（三包头部 3→1、闭包残留判据 4→1、等待循环 4→1、平台自测 3→1+3 小份）。
- 收益：脚本规范从「不存在」变成**可失败的门**：S4 能复现历史缺陷形态（把 `INSTALL_WAIT_SECONDS` 塞回 win 脚本即被判红，实测），S5 能拦影子副本（A5 的 `log_has` 形态），S6 把全仓 shell 面交工具而不是靠人眼。首轮 shellcheck 报 15 处（其中 1 处是真缺陷——`packaging-common.sh` 里未使用的局部变量 `want`；其余为需行内豁免的误报与真实风格问题），actionlint 在 `-S warning` 下报 1 处（`package.yml` 的 `for i in 1 2 3` 未用 `i`），全部已修/已豁免。
- 收益：83 处从不执行的冒烟自测断言接线进 CI（条数见 Testing；含包布局三平台分支与 Inno 模板渲染）——「资产已写好但没人跑」的状态结束。
- 代价：CI `docs` job 每次 push 多约 1.5 分钟（装两个工具 + 8 份夹具）；本地 pre-commit 多一条 **需要 shellcheck 的门**——缺工具即判红并提示 `scripts/install-linters.sh`（门禁悄悄不执行 = 假绿，宁可挡住提交）。
- 代价：`package-windows.sh` 的 Inno 段从「内联全貌」变成「模板 + 渲染器」两处，读代码要跳一次；换来模板可被离线夹具验证（渲染后残留占位、空语言行、含空格路径三种形态已钉）。
- 代价：`smoke-linux-rpm.sh` 的容器内脚本成为 `scripts/` 下第二个非入口脚本（第一个是 hooks），其容器路径 `/smoke-lib`、`/smoke-inner.sh` 是硬编码的挂载点——改挂载必须同步改两处，已在双方注释互指。
- 边界：脚本规范对 `.py` 门禁脚本无约束（尺寸闸只覆盖 shell；`verify-review-brief.py` 815 行仍在 Python 面），Python 层去重是方案 §5.4／F 批的事。
- 边界：S2 的 250 行是**授权值**不是最优值——Google 的 100 行建议在本仓的行数分布下会把一半工具脚本判违规；超限的处理是拆文件（本批已演示 god script 拆分），不是迁语言。
- 边界：旋钮声明的「声明面」含 `docs/` 全文——把旋钮名写进任意文档即可放行，这是刻意的信任面（判据只能证「有声明」，不能证「声明在正确的家」）。
- 未覆盖：①容器腿的**容器内脚本**已由离线夹具覆盖（含窗口契约与判定链），但 docker 起容器本身仍只能靠 tag/dispatch 的 linux 腿实跑（该证据见 Testing「三平台 dispatch 实跑」）；②action 仍全部浮动大版本（`checkout@v4` 等），SHA 钉版按方案 D3 列为后续独立项——`freshness.yml` 退役后**当前无任何 `schedule:` 触发**，浮动钉版无监控这点在本批未修；③Python 门禁共享模块与两道最薄门禁（`verify-doc-budgets.py`/`verify-md-links.py`）的 `--self-test` 缺口仍在（F 批）。
- 与整改方案的关系：本批即方案 §5.1–§5.3（批次 C 的脚本面）＋ §6.4（B 批残余的 actionlint/shellcheck）＋ A3/A4/A5 的延后项；§5.4（Python 门禁共享模块）按编排归 F 批，不在本批。

## Testing

- **离线夹具（本地全跑绿；CI `docs` job 每次 push 跑全集；**现行计数只在此处，且为本地口径**）**：`verify-shell-standards.sh --self-test` 7、`verify-package-layout.sh --self-test` 10、三包 `--self-test` 11/11/20、三平台冒烟 `--self-test` 56/55/53、`smoke-linux-rpm-inner.sh --self-test` 3——合计 **226 条断言**，每条判据的判红侧与放行侧同批在位（此处的「共用面」指冒烟三入口的共用夹具 53 条，按平台文件调用点 3/2/0 计；与 Decision 四的「全仓共用面 46 条」统计域不同）。其中 3 条（`trap-keeps-ok` / `trap-reclaims-tmp` / `trap-keeps-fail`）为 [smoke-trap-exit-status-flip](../bug-fix/2026-09-28-smoke-trap-exit-status-flip.md) 补上的 EXIT trap 契约锁。CI 侧实测为 `52 / 49 / 49`（该 job 不装 imagemagick）：`scripts/lib/smoke-selftest-verdict.sh` 的见证夹具走 `skip` 分支（三平台各 −4），mac 另有 `scripts/smoke-install-macos.sh` 的同类分支（再 −2）。
- **门禁可失败性实测（不止夹具）**：①把 `INSTALL_WAIT_SECONDS` 塞回 `smoke-install-windows.sh`（复现 A 批死旋钮形态）→ `verify-shell-standards.sh` 判红并点名该旋钮，还原即绿；②`publish` 目录塞 `resources/runtime` → `package-linux.sh --stage-only` 经共享布局断言判红（rc=1），干净目录即绿；③撤掉 `packaging-common.sh` 的 `source common.sh` → 其自测判红（诊断文本缺失）；④容器腿改名缺陷形态（容器内不解析窗口）由 `smoke-linux-rpm-inner.sh --self-test` 覆盖；⑤`verify-package-layout.sh` 缺 `--platform` 判红（exit 2）。
- **`--stage-only` 三平台实跑**（假 publish 目录；`artifacts/` 为本地态）：linux / macos / windows 三份均「布局断言通过（无闭包残留、插件资源齐、主程序在位）」。
- **静态检查**：`shellcheck -S warning` 全仓 0 告警（26 文件，含 `scripts/lib/**`）；`actionlint -shellcheck="shellcheck -S warning"` 0 告警；两者版本钉 0.11.0 / 1.7.12，`install-linters.sh` 的 sha256 校验实跑通过（本地与 CI 同一条安装路径）。
- **文档门禁**：`verify-adr-format.py`、`verify-cookbook.py`、`verify-doc-budgets.py --manifest …`（`docs/script-standards.md` 330/500）、`verify-md-links.py`、`verify-governance.py`、`verify-handoff-structure.py`、`verify-readme-badges.py`、`verify-skill-format.py`、`verify-code-health.py --enforce`、`verify-code-conventions.py --enforce`、`verify-ui-copy.py` 全绿。
- **三平台 dispatch 实跑（`36371907647`，commit `bbb8271`）**：`ci.yml` push 轮 `36371903414` success；`package.yml` 五腿——mac 双 rid ✅✅、win ✅、linux 双腿 ✅。**linux 面曾在本批（`39c860a`）上三连 ❌**（`36363806570`/`36365626333`/`36366396555`：腿结论全绿、step 静默 `exit 1`），根因是本批引入的 `common.sh` EXIT trap 翻码 + 临时路径漏回收，修复见 [smoke-trap-exit-status-flip](../bug-fix/2026-09-28-smoke-trap-exit-status-flip.md)。真 docker 里的 rpm 容器腿、真 runner 上的 mac/win 冒烟与 `GITHUB_PATH` 生效路径均已覆盖。
- 本批零 `src`/`tests` 变更，`dotnet build/test/format` 按变更面不适用（未跑即未跑，不列为证据）。

## Related

- [docs/script-standards.md](../../../../docs/script-standards.md)：本批落地的规范正文（条款的机器强制面即 `verify-shell-standards.sh`）。
- [ci-gate-honesty](2026-09-28-ci-gate-honesty.md)：「每道门必须有能失败的路径 + 自测/证伪用例」的姐妹批（B-1）；本批把该口径施于脚本面。
- [ci-package-workflow-unification](2026-09-28-ci-package-workflow-unification.md)：B-2 三流合一，其 §6.4 残余（actionlint/shellcheck）在本批收口。
- [artifact-verification-chain](2026-08-24-artifact-verification-chain.md)：`verify-package-layout.sh` 与三平台冒烟的立项 ADR；本批改其调用形态（`--platform` 必填）与实现分层。
- [shell-settle-behavior-gate](../bug-fix/2026-09-27-shell-settle-behavior-gate.md)：落定门语义的来源；本批只搬实现（等待/判定入共享库），语义一字不动。
- [macos-cookie-grace-reload-and-witness-gate](../../archived/bug-fix/2026-09-27-macos-cookie-grace-reload-and-witness-gate.md)：`wait_verdict` 与像素见证的由来，即 Decision 六的取舍对象。
- [gui-freeze-forensics-probe](2026-08-28-gui-freeze-forensics-probe.md)：被退役探针的立项 ADR（Consequences 已记退役指针）。
- [smoke-trap-exit-status-flip](../bug-fix/2026-09-28-smoke-trap-exit-status-flip.md)：本批引入的缺陷与其修复（linux 冒烟双腿静默翻红 + 临时路径漏回收）；其三平台自测的计数已按修后现实回写本段。
- `.plan/整改方案-三平台震荡后结构清理-2026-09-27.md` §5（本地工作文档，未入库）：本批的编排来源。
