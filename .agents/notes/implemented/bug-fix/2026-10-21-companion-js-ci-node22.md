# Agent Note: companion JS 测试腿的 CI 命令口径（目录位置参数的行为随 Node 版本变化）

Status: implemented

中文（双语暂不启用；启用时恢复 .md 与 .zh.md 镜像 + .i18n.yaml 配对机制）

Review: FULL/2026-10-21#3/R1=ok R2=ok R3=ok

评审：3 轮（首轮 + 2 轮验轮）。首轮 R1 0 Blocker + 5 Suggestion、R2 1 Blocker + 1 Suggestion、R3 2 Blocker + 2 Suggestion；第 2 轮 R1 1 Blocker + 1 Suggestion、R2 0 Blocker + 4 Suggestion、R3 0 Blocker + 2 Suggestion；第 3 轮 R1 0 Blocker + 3 Suggestion、R2 1 Blocker + 2 Suggestion、R3 1 Blocker + 1 Suggestion。每路 3 轮用尽（轮次上限 3，第 4 轮需用户显式授权），未再开第 4 轮：第 3 轮的两处 Blocker（R2 报的符号链接逃逸、R3 报的上游叙述反向）属「上一轮修复引入的新缺陷」与「依据被证伪」，按本篇契约当轮修毕；Suggestion 由主会话逐条书面裁定后收口，本行 `ok` 取「本轮 0 未闭合 Blocker 且建议已书面裁定」义。

- 第 3 轮 R3 B1（本篇把上游 #64555 写成「Windows 上的现象、回归自 v20」，而 issue 报告者已在评论里更正为非 Windows 专属并有 Linux 复现、维护者与报告者把翻转点定在 v21.0.0，机制与本篇 Linux 侧失败相同）：**已修**——本篇改写为上游实况：翻转点 v21.0.0（发现逻辑改走内部 glob，目录模式匹配到目录自身，runner 把它当测试文件 spawn）、目录搜索在 v26.8.0 恢复（提交 `1ad7ca310a` 随 26.8.0 发布，changelog 记的是 #64637 而该 PR 页面显示 closed 且未合并）、#64659 仍开着；本机三个观察点（v20.19.0 可用 / v22.23.3 失效 / v26.8.1 可用）与该叙述自洽，v21–v26.7 的逐版行为本机未复核。
- 第 3 轮 R2 B1 / R3 S1（`test/layout.test.js` 的 `findFiles` 只收 `isFile()`，文件型符号链接被整体跳过，配套件以链接形态误放时用例计数漂到 21 而 job 仍绿）：**已修**——自检改为 `findEntries` 收集 `{path,name,isFile,isDirectory,isSymbolicLink}`，`test/` 树内只允许常规 `*.test.js`（符号链接一律判违规）。
- 第 3 轮 R1 S1 / R2 S1（把 Node 的发现规则复述成「`test/` 下每个文件都被载入」，`test/data.json` 并不被载入）：**已修**——自检头注释与本篇只写本仓自己的布局政策，不复述 Node 的规则。
- 第 3 轮 R1 S2 / R2 S2（漂移面没盖 `test/` 树外：`client/stray.test.js`、`testkit/support.test.js` 都被 Node 按名字拾走）：**已修**——自检增政策「`test/` 之外不得有 `*.test.js` 文件」。
- 第 3 轮 R1 S3（收益句称「没有第二份会漂的字面」，而 `ci.yml` 与 `docs/testing.md` 各写着同一字面且无门禁绑定）：**已修**——收益句改述事实；命令字面共三份（`ci.yml` 一份、`docs/testing.md` 两份）、无机器门禁绑定这一残留代价写进边界条。
- 第 2 轮 R1 B1 / R2 S1（布局自检只扫 `test/` 顶层且只认 `.js`，`test/sub/helper.js`、`test/support.cjs` 能溜过去而 job 仍绿 → 本篇「配套件误放会被布局自检挡下」被证伪）：**已修**——断言 1 改为递归遍历 `test/` 树；本篇里复述 Node 发现规则的句子降为指针，Consequences 与旧篇的承诺句同步为本仓政策。
- 第 2 轮 R1 S1（`package.json` 的 `scripts.test` 是「怎么跑 companion JS 测试」的第二家，无消费方、无相等性门禁，且属 LIGHT 档文件）：**采纳收敛选项 (A)**——移除 `scripts.test`（`package.json` 不再有 `scripts` 键），命令的执行面在 `ci.yml`、操作面在 `docs/testing.md`；本篇增补「保留 `package.json` 脚本」备选与落败理由，旧篇同步改写。
- 第 2 轮 R2 S2（上游 #64555 的平台限定缺失）：**已修（方向随第 3 轮 R3 B1 一并更正）**——限定不是「Windows 上」，而是「报告者首报平台为 Windows、评论区已更正为非平台专属」。
- 第 2 轮 R2 S3（glob 位置参数的下界 21 未实测，与本篇对同类版本边界的口径不一致）：**已修**——改实测口径（v20.19.0 失败退出码 1，v22.23.3 与 v26.8.1 通过）+【推断 · 未证】限定；上游依据另查得：glob 能力随 v21 的 semver-major 提交 `7cd4e70948`（#47653）落地。
- 第 2 轮 R2 S4（Testing 段的括注把插件目录 cwd 形态标成「CI 旧命令形态」，与 CI 日志里的路径对不上）：**已修**——Testing 段逐条标注 cwd 与命令形态。
- 第 2 轮 R3 S1（「Node 18+ 行为一致」无观察点）、S2（Testing 缺 CI 同版本的强证据）：**已修**——收窄到实测三版本，并补 v22.23.3 在新形态下 20/20 的本机证据。
- 第 2 轮 R2 B1 / R3 B2（`Review:` 证据行缺失，FULL 档自证被拦）：**已修**——本行即证据行（今日第 3 个 FULL 批）。
- 第 1 轮 R1 S1（CI 字面与 `package.json` 脚本两个家）：**采纳（改为删脚本）**——CI 改跑 `npm test` 的备选**驳回**：那会把门禁的执行内容交给 LIGHT 档文件，改一行即可让 job 空跑而不触发评审。
- 第 1 轮 R1 S2（`## Alternatives considered` 漏两条更简备选）：**采纳**——补「CI 跑 `npm test`」与「`test/` 改名 `tests/`」两条。
- 第 1 轮 R1 S3（覆盖率跟值是搭车、既无台账行也无入库出处）：**采纳**——跟值台账（09-12 篇 `## Related` 第一节）补 2026-10-21 一行（`39539692307` / 6887/10268 / 1093/1093 / 67.07%）。
- 第 1 轮 R1 S4（ci.yml 注释复述本篇的机制且丢掉未证限定）：**采纳**——注释只留本篇指针与一句结论。
- 第 1 轮 R1 S5（HANDOFF 里的 64.77% 过期副本）：**采纳**——会话收尾同步（本批不入库，属本地工作文档）。
- 第 1 轮 R2 S1（本仓无门禁断言配套件位置，配套件误放时 18→19 漂移不会变红）：**采纳**——JS 腿内加布局自检（`test/layout.test.js`，2 条用例）。
- 第 1 轮 R2 B1（同第 1 轮 R2/R3 的证据行问题）：**已修**——本行即证据行。
- 第 1 轮 R3 B1（本篇把「目录位置参数 → MODULE_NOT_FOUND」写成「Node ≥21 起」的既定事实，本机 v26.8.1 实测该形态可用）：**已修**——本篇改写为实测矩阵（v20.19.0 / v22.23.3 / v26.8.1），版本结论收窄到观察点并标【推断 · 未证】。
- 第 1 轮 R3 S1（「Node 18+ 行为一致」无观察点）、S2（Testing 缺 CI 同版本强证据）：**采纳**——收窄到实测三版本并补 v22.23.3 的本机证据。

## Problem

[companion 客户端 JS 测试腿](../testing/2026-10-21-companion-client-js-tests.md) 的 `companion-js` job 在 CI 上是红的：run `39539692307` 的该 job 退出码 1，日志原文 `Error: Cannot find module '/home/runner/work/dotnet-deepseek-harness-desktop/dotnet-deepseek-harness-desktop/plugins/dsh-desktop-companion/test'`（`MODULE_NOT_FOUND`），Node 版本 v22.23.3；同一 run 的 `changes`/`docs`/`build-test`（三平台）job 全绿。

落败的是命令形态而不是测试：同一份测试在本机 Node v26.8.1 上 20/20 通过。目录位置参数（`node --test <目录>`）的行为随 Node 版本变化，本机实测矩阵（每条命令的 cwd 逐条标注见 Testing 段）：

| Node | `node --test <目录>` | `node --test`（插件目录为 cwd，无位置参数） |
|---|---|---|
| v20.19.0 | 20/20 通过 | 20/20 通过 |
| v22.23.3（CI 钉的版本） | `MODULE_NOT_FOUND`，退出码 1 | 20/20 通过 |
| v26.8.1（本机） | 20/20 通过 | 20/20 通过 |

上游把这个现象记在 [nodejs/node#64555](https://github.com/nodejs/node/issues/64555)：标题带着首报平台（Windows）与「regression since v20」，正文却写「(since v21)」；报告者在评论里说明它并非 Windows 专属（他在 Linux x64 上复现），维护者与报告者把翻转点定在 v21.0.0——发现逻辑改走内部 glob，目录模式匹配到目录自身、runner 把它当测试文件 spawn，于是报 `MODULE_NOT_FOUND`（上游的说法，非本机验证）。这正是本篇 Linux 侧失败的同一机制。目录搜索在 v26.8.0 恢复（提交 `1ad7ca310a`，随 26.8.0 发布；changelog 记的是 #64637，而该 PR 页面显示 closed 且未合并），[#64659](https://github.com/nodejs/node/pull/64659)（支持目录参数）仍开着。因此 v20.19.0 可用、v22.23.3 失效、v26.8.1 又可用这三个观察点与上游的版本叙述自洽；v21–v26.7 的逐版行为本机未复核。

## Decision

CI 的 `companion-js` job 改用 Node 的默认测试发现：在 `plugins/dsh-desktop-companion/` 目录下跑 `node --test`（无位置参数）——这是上表三个观察点里唯一都通过的命令形态。插件的两个测试文件都命名为 `*.test.js`，Node 的默认发现会载入它们（发现式样以 Node 官方文档为准，本篇不复述：复述即多一份会随版本漂的副本）；命令字面共三份——`ci.yml` 一份（执行面）、`docs/testing.md` 两份（操作面），`package.json` 不带 `test` 脚本（那会是第四份，没有消费方，留着只会漂）。三份字面相同这件事没有机器门禁绑定，靠评审逐字核对（见 Consequences 的边界条）。

测试配套件（DOM 桩、可控时钟、Cordis 上下文、React 桩）从 `test/harness.js` 移到 `plugins/dsh-desktop-companion/testkit/harness.js`：配套件是 `.js` 文件，留在 `test/` 树内会被 Node 的默认发现当测试文件载入、用例计数被算进去（配套件移出、布局自检加入之前的本机计数是 19 = 18 条行为用例 + 1 个零断言的配套文件），移出后 `test/` 只剩 `*.test.js` 的真测试文件。

配套件的位置由 JS 腿内的布局自检钉住（`test/layout.test.js`，2 条用例），断言的是本仓自己的布局政策，不复述 Node 的发现规则：① `test/` 树内只允许常规的 `*.test.js` 文件（递归，符号链接一律判违规）；② `test/` 之外不得有 `*.test.js` 文件；③ 插件内只有一个名为 `test` 的目录。这条政策严于 Node 的发现规则，Node 的规则怎么变都成立。缺了它，配套件被当测试文件载入时用例计数会从 20 漂到 21（job 只看退出码，不会变红），`test/` 之外的 `*.test.js` 也会被 Node 按名字拾走。

## Alternatives considered

- **CI 改跑 `npm test`**（让 `package.json` 成为唯一事实家）：落败——门禁的执行内容就落在一个不在 FULL 评审触发面里的文件上，改它一行即可让 job 空跑而不触发评审；命令字面留在 `ci.yml` 时，改它要带 FULL 档评审证据。
- **保留 `package.json` 的 `test` 脚本作本地入口**（与 CI 字面同形）：落败——它没有消费方，也没有任何门禁断言它与 `ci.yml` 的字面相等，本地口径因此会静默漂离 CI；命令的唯一家留在 `ci.yml`（执行面）与 `docs/testing.md`（操作面），本决定据此移除该脚本。
- **把 `test/` 改名 `tests/`、只留一个目录**：落败——实测（Node v22.23.3）：目录名改成 `tests/` 后，一份真测试若改名成 `tests/client.js`（名字不合规）就一条都不跑（计数只剩 2 = 布局自检自身的 2 条用例，且自检因目录名不符而红），改名无声无息；同样的文件放在 `test/` 形态下（`test/client.js` 与 `test/client.test.js` 并存）会被载入并跑出 18 条用例（计数 38 = 18 + 18 + 2），命名错会浮出来。
- **给 CI 命令加引号 glob**（`node --test "plugins/dsh-desktop-companion/test/**/*.test.js"`）：落败——实测 v20.19.0 报 `Could not find '.../test/**/*.test.js'`、退出码 1（该形态是 Node 21+ 的能力；【推断 · 未证】下界落在 v21–v22 之间，v21 未实测），v22.23.3 与 v26.8.1 才 20/20 通过；它把 `node:test` 的支持下限从稳定化的 20 抬到 21，且依赖 shell 的引号语义；无位置参数形态在实测的三个版本上都通过。
- **用 shell 展开成显式文件列表**（`node --test test/*.test.js`）：落败——展开行为随 shell 与平台而异，且命名不匹配 `*.test.js` 的新测试文件会被静默漏跑；无声漏跑比报错更难发现。
- **在 `test/` 下加 `index.js` 垫片让目录路径能被当模块解析**：落败——垫片只绕开报错，位置参数的语义差异仍被掩盖，且垫片本身会被默认发现当测试文件再计一次。
- **把 CI 钉的 Node 升到 26.8+**：落败——目录搜索在 v26.8.0 才恢复，v24 线经上游复现失败（报告者在 v24.18.0 上复现），v25 线按翻转点与恢复点推断同坏（【推断 · 未证】，v25 未实测）；要靠升版本救回旧命令就得把 CI 的 Node 钉版抬到 26.8+；而 26 线还不是 LTS 线（nodejs.org 的发布索引：v26 无 LTS 标记，v24 是「Krypton」、v22 是「Jod」），把 CI 钉到一条非 LTS 线上换回旧命令形态不划算；改命令形态则三个观察点都通过，与版本无关。
- **配套件留在 `test/`**：落败——`test/` 树内只允许 `*.test.js` 是本篇的布局政策，配套件留在原处会被布局自检判红（用例计数也会从 20 漂到 21），而不是「接受一个不同的计数」。

## Consequences

- 收益：命令形态在实测的三个版本（v20.19.0 / v22.23.3 / v26.8.1）上都与版本无关；新测试文件按 Node 约定命名即自动纳入，不存在静默漏跑；配套件误放（`test/` 树内的非 `*.test.js` 文件、符号链接）或 `*.test.js` 落到 `test/` 之外，都会被布局自检挡下。
- 代价：测试文件的引用路径改一行；配套件一律放 `testkit/` 且不得命名为 `*.test.js`（Node 的默认发现按名字在任意目录匹配 `*.test.js`，实测 `testkit/support.test.js` 会被载入），这条由布局自检钉住。
- 边界：CI 仍钉 Node 22（打包腿另有自己的 Node 钉版，与本篇无关）；本篇不抬高 `node:test` 的版本要求（Node 18 实验、20 起稳定；本机实测覆盖 20/22/26 三个点）；命令字面共三份（`ci.yml` 一份、`docs/testing.md` 两份），机器门禁不绑定它们相等（本仓没有「CI 命令 ↔ 文档口径」的校验面），改一处要同时改另两处；命名为 `*.test.js` 却不是测试的配套件，自检分辨不出（它就是政策允许的形状），只能靠命名纪律。

## Testing

- 本机（Node v26.8.1，插件目录为 cwd）：`node --test` → 20/20 通过（18 条行为用例 + 2 条布局自检）；同 cwd 下 `node --test test/` 也 20/20 通过——这正是「目录位置参数形态随版本变化」的观察点（CI 的旧命令是仓库根 cwd 下的 `node --test plugins/dsh-desktop-companion/test/`，形态等价）。
- CI 同版本（Node v22.23.3）：插件目录为 cwd 时 `node --test` → 20/20 通过、退出码 0；插件目录为 cwd 时 `node --test test/`、以及仓库根为 cwd 时 `node --test plugins/dsh-desktop-companion/test/`（CI 的旧命令）均报 `MODULE_NOT_FOUND`、退出码 1（复现 CI 红）。
- Node v20.19.0：插件目录为 cwd 时 `node --test` 与 `node --test test/` 都 20/20 通过。
- 上游依据（读上游仓库所得，非本机验证）：glob 位置参数随 v21 的 semver-major 提交 `7cd4e70948`（#47653）落地，见 `CHANGELOG_V21.md`；目录搜索的恢复随 v26.8.0 发布（提交 `1ad7ca310a`，changelog 记 #64637，该 PR 页面显示 closed 且未合并），见 `CHANGELOG_V26.md`；#64659（支持目录参数）仍开着。Node 发行线的 LTS 状态见 nodejs.org 的发布索引。
- 布局自检非空转（Node v22.23.3，仓库外的插件副本）：`test/helper.js`（普通文件）、`test/fixtures/helper.js`（嵌套）、`test/support.cjs`、符号链接 `test/helper.js -> ../testkit/harness.js` → 布局自检都判红（用例计数 21 而 job 仍绿，正是这条自检存在的理由）；`test/data.json` → 判红（`test/` 树内只允许 `*.test.js`）；`client/stray.test.js`、`testkit/support.test.js` → 判红（`test/` 之外不得有 `*.test.js`）；空目录 `test/emptydir/` → 不误判（保持 20/20）。每种形态移除后恢复 20/20。
- CI：run `39539692307` 复现红（Node v22.23.3，`MODULE_NOT_FOUND`）；修复随本篇对应的提交上 CI，由 `companion-js` job 复验。

## Related

- [companion 客户端 JS 测试腿](../testing/2026-10-21-companion-client-js-tests.md)：本篇修的是它的 CI 跑法与配套件位置。
- [窗口脚本求值端口](../architecture/2026-10-21-window-script-target-port.md)：同批的 .NET 侧测试面补洞。
