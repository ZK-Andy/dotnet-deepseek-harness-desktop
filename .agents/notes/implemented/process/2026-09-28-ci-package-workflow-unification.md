# Agent Note: CI 打包三流合一——reusable workflow + composite action，发布走 needs

Status: implemented

Review: FULL/2026-09-28#3/R1=ok R2=ok R3=ok

评审：R1（简化面）4 轮、R2（代码面）4 轮——第 2–4 轮的结论均落在本批自身措辞的自相矛盾面（勘误计数、写错的「不变」、门禁扩面后别处描述未跟改），已随批改正，用户按「证据行判据 = 0 Blocker」叫停续轮；R3（ADR/文档面）本批首轮，1 Blocker + 8 Suggestion。逐条裁定：

- **Blocker**（残余缺口①把「推送后要跑的 dispatch」写成已取得的替代证据）：**已修**——改为「机器替代证据 = `--self-test`；分支 ref 上的 dispatch 待本批推送后补跑，实跑记录见 Testing」。
- **S1**（Problem 表首行「`permissions` + `concurrency` + `workflow_dispatch` 版本输入头」记 74 行，与自述口径不符）：**采纳**——按该行口径复算为 43（linux 11 + macos 16 + windows 16）。
- **S2**（同表 checkout 行标注含 linux `setup-node`，数字却只算 18）：**采纳**——改 21（3×6 + 3）。
- **S3**（「2026-09-26 至 09-27 两天里…三次」与 git 史不符）：**采纳**——三文件同改的三次提交是 `f352280`/`9bd49a3`/`c608ab0`（09-25/09-27/09-28），已改写为具名。
- **S4**（`Erratum` 的新触发面只写在本篇，规则家未同步）：**采纳**——[`.agents/notes/README.md`](../../README.md) 的勘误通道收编两种失效面（依据被证伪 → 补行后归档；落点换家 → 正文与决定不动），规则家与用法同批一致。
- **S5**（`settle-window-and-arm64-freeze-retirement` 属第 17 处勘误候选）：**驳回**——该篇 Problem 段描述的是被退役机制的当时状态、Testing 段是 run 记录，正落在本批自定判据明列的「历史叙述不改」豁免面（当时的文件名是对的），补勘误反而误导。
- **S6**（cookbook「reusable workflow 四坑」复述本篇 Alternatives，属 rationale 第二家）：**部分采纳**——删去第 ② 条里「不需要轮询产物也不需要跨 run API」这段已由本篇承接的取舍复述并链回本篇；四条坑的症状与落地做法保留（踩坑记录是 cookbook 的家）。
- **S7**（`docs/testing.md` 复述「Re-run failed jobs」恢复流程）：**采纳**——删该子句，procedure 归 `release-flow.md` 与 `release.yml` 注释。
- **S8**（`docs/development.md` 称 `release.yml` 为「tag 触发」，漏掉手动 dispatch 这半个触发面，而本篇 Decision 二依赖它）：**采纳**——补「手动 dispatch 在分支 ref 上只出包不发布」。

R3 另独立用 `gh` 与 base 文件复核了本篇全部数值断言（176/123/133=432、`36325106379` 的 6m43s/7m01s、11 条自测、治理扫描面、16 处勘误恰等且 16 篇均只增不删、`page-verdict-gate` 勘误指向成立），并确认 Decision 全为现在时、Alternatives 覆盖三案、durable 文档已零残留旧文件名引用。

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

三平台打包是三份逐字抄出来的复制品（`package-linux.yml` 176 行 / `package-macos.yml` 123 行 / `package-windows.yml` 133 行 = 432 行），同一段前置与头部各存三份：

| 逐字重复块 | 份数 | 旧行数 |
|---|---|---|
| `permissions` + `concurrency` + `workflow_dispatch` 版本输入头 | 3 | 43 |
| checkout + setup-dotnet（+ linux 的 setup-node） | 3 | 21 |
| 「确定版本」步（含 tag/输入一致性判据） | 3 | 57 |
| 冒烟截图/日志上传对（仅 artifact 名不同） | 5（腿） | 10 步 |

后果不是「行数多」，是**改一次要同步三处**：2026-09-25/09-27/09-28 三次提交（`f352280`/`9bd49a3`/`c608ab0`）里该尾块被三文件同改。合批时又实测出两处既有缺陷：

- **自签旋钮恒不生效**：作业 env 取 `github.event.inputs.self_sign`，而打包脚本的门是 `SELF_SIGN=1`（`package-macos.sh:87`、`package-windows.sh:83,261`）——boolean 输入插值出来是 `true`/`false`，永不等于 `1`。旋钮自 2026-08-20 接进 CI 起就是死的。
- **tag/输入一致性判据只在本地手跑过**：[CI 门禁诚实化](2026-09-28-ci-gate-honesty.md) 本已把 `::warning::` 改成 fail loud，但该步挂在 `if: startsWith(github.ref, 'refs/tags/v')` 上，dispatch 到不了它的失败分支（该篇记为残余缺口）。

发布侧（`release.yml`）另有三处结构代价：发布作业靠 `for i in $(seq 1 150); sleep 10` 轮询 artifact 存在性（run `36325106379` 实测：该作业 7m01s 里 **6m43s** 是这段等待，从 14:13:06 轮询到 14:19:49 才就绪）；产物经 `gh api` 跨 run 下载（2 次 API + 内嵌 python 解压 5 个产物）；失败检测是手写的 `check_failed_runs` 逃生检查——因为轮询只认产物存在性，包腿失败时产物永不出现，只能靠额外查询辨认。

## Decision

**一、三份 package workflow 合一为 `package.yml`（`workflow_call` + `workflow_dispatch`），三平台 job 同文件。**

- 共用前置收进 [`.github/actions/package-setup`](../../../../.github/actions/package-setup/action.yml)：setup-dotnet（+ 可选 setup-node）+ 版本解析，三行调用替代三份抄写；`actions/checkout` 仍留各 job 里更早的一步——本地 action 的 `action.yml` 在**该 step 自己开跑时**才从工作区读取（job 准备阶段对 `./` 自仓 action 显式跳过），把 checkout 收进 action 内会让五条腿死在 `Can't find 'action.yml'`（见 Alternatives）。
- **版本解析与 tag/输入一致性判据的唯一实现** = [`scripts/package-version.sh`](../../../../scripts/package-version.sh)（解析 + 一致性判据 + `--self-test`）：优先序固定为 `tag > 输入 > csproj <Version>`，三层全空 fail loud；ref 为 `refs/tags/v*` 时版本只认 tag，dispatch 输入非空且不一致即 `::error::` 点名两个版本号并 exit 1。判据搬出 workflow 的 `if:` 外壳，于是**每次 CI 都跑**：`--self-test` 以假 ref/输入/csproj 实跑本脚本 11 条断言（逐条见 Testing），执行点在 `ci.yml` 的 `docs` job（与 `verify-ui-copy.py --self-test` 同档）。判据面同时**从 linux 单腿扩到三腿**（旧形态只在 linux 腿有这一段，另两腿无 tag/输入校验）。
- **自签旋钮接正**：`SELF_SIGN: ${{ inputs.self_sign && '1' || '0' }}`——boolean 输入映射成脚本认的 `1`/`0`，dispatch `self_sign=true` 首次真正生效；tag 发布恒不自签（调用方显式传 `self_sign: false`）。
- **`concurrency` 组名三段**：`${{ github.workflow }}-${{ github.event_name }}-${{ github.ref }}`，`cancel-in-progress` 只在 `workflow_dispatch` 为 true。被调用执行时 `github.workflow` 取**调用方**名（文档明示），故段位作用各不相同：**事件段**把被调用腿（`release-<事件>-<ref>`）与调用方自身的组（`release-<ref>`）分开——删掉即同组，两侧 `cancel-in-progress` 相反时死锁（`Canceling since a deadlock for concurrency group`）；**workflow 名段**把被调用腿与「手动 dispatch 本 workflow 的预览轮」（`package-…`）分开，否则后者的 cancel 会掐掉前者正在跑的发布作业。
- 资产命名矩阵（`linux-{amd64,arm64}-packages` / `macos-{arm64,x64}-packages` / `windows-x64-packages` 及其内部文件名）**不动**——那是 `release-preflight.sh` 的契约。
- 冒烟截图/日志上传步**留在各 job 内联**，不收进 composite（理由见 Alternatives）。

**二、`release.yml` 用 `needs:` 取代轮询，发布作业与包腿同 run。**

- 包腿经 `uses: ./.github/workflows/package.yml`（`workflow_call`）调用；发布作业 `needs: package`，产物用 `actions/download-artifact@v4`（`pattern: '*-packages'` + `merge-multiple`）从**同一 run** 取——被调用方上传的 artifact 与调用方共享 run，无需 token/run-id。
- 失败判定由依赖关系结构性承担：包腿任一失败（含安装冒烟），发布作业根本不启动；`for i in $(seq 1 150)` 轮询与 `check_failed_runs` 逃生检查一并删除，跨 run API 下载与内嵌解压删除。
- 发布作业仍是唯一写 `contents: write` 的地方（job 级 `permissions`），调用方 workflow 级只给 `contents: read`。
- 发布作业的 `if: startsWith(github.ref, 'refs/tags/v')` 使**手动 dispatch `release.yml` 在分支 ref 上只出包不发布**，tag ref 上走完整发布；恢复路径 = 在该 tag 的 run 上「Re-run failed jobs」（产物仍在同 run 内，7 天留存）。

**三、同批的 CI 卫生项**（都在本批重写的文件里）：`ci.yml` 显式声明 `permissions: contents: read` + `pull-requests: read`（`changes` 作业的 dorny/paths-filter 在 PR 事件下拉变更文件所需），不再跟随仓库默认设置；`package.yml` 的 npm 缓存键保留无 hash 形态但写明理由（缓存的注册表下载内容无仓库侧可表征输入，陈旧只降命中率，cacache 内容寻址不产陈旧产物），同时删掉恒不参与的前缀 `restore-keys`；`DOTNET_NOLOGO`/`DOTNET_CLI_TELEMETRY_OPTOUT` 提为 workflow 级 env（原三份各写一遍）；删掉注释里的版本变更史（arm64 曾停发/恢复、旧 release 方案竞争），可判定的约束留下。

**四、治理门禁的扫描面补上 composite action。** [`scripts/verify-governance.py`](../../../../scripts/verify-governance.py) 原只 glob `.github/workflows/*.yml`，且 `_walk_steps` 只沿 `jobs`/`steps` 下潜——新引入的 composite action（`runs.steps` 同样是可执行脚本体）落在这道「`run:` 禁插值事件载荷/env 上下文」的门外。本批把扫描面扩到 `.github/actions/**/action.y*ml` 并让 `_walk_steps` 认 `runs`，自测补两条夹具（composite 里的违规形态判红、合法 `env:` 通道形态不误报）——否则本批「新文件无注入面」的说法在这道门上无人复核。执行点不变（`governance.yml` 的治理门禁步与本地 `pre-commit` 同源调它）。

## Alternatives considered

- **三个薄 caller 各自 `workflow_call` 调 `package.yml`**：落败——文件数不降反升（4 个），且「改一次同步三处」的老问题原样保留；`workflow_dispatch` 预览面也没收敛。
- **只抽共用件、不合并文件**（保留三文件 + composite + script）：落败——头部/dispatch 输入/上传对仍是三份；且拿不到本批最大收益（同 run + `needs:`）。
- **合成单文件**（把发布作业也放进 `package.yml`）：落败——发布轮与预览轮会共享同一 `concurrency` 段，预览的 `cancel-in-progress: true` 会掐掉发布；权限也要在一处混装 `read`/`write`。分文件后并发与权限各归其位。
- **把冒烟证据上传对也收进 composite**：落败——composite action 内的 `if: always()` 在**工作流被取消**时不执行（runner 已知限制），会让「取消时也留尸检证据」这一既有性质退化；且判据要的 `steps.smoke.outcome` 是调用方 step 引用，composite 看不见。同文件内的重复比跨文件重复的同步风险低一个量级，不值得用证据保全换。
- **把 `actions/checkout` 也收进 composite**（首版实现如此，两路评审独立判 Blocker）：落败——本地 action 的 `action.yml` 在该 step 自己开跑时按 `$GITHUB_WORKSPACE` 读取（job 准备阶段对 `./` 自仓 action 显式跳过，下载路径也只发生在非 `./` 引用），而 composite 内那一步执行时工作区还是空的，五条腿全部死在该步（`Can't find 'action.yml' … Did you forget to run actions/checkout before running your local action?`）。代价是三条腿各多 3 行 checkout，换来的是不依赖 runner 内部时序的既知行为。
- **把 `SELF_SIGN` 映射也提到 workflow 级 `env`**（评审建议，与 `DOTNET_*` 同法）：落败——收益只是省一处重复表达式，代价是 workflow 级 `env` 引用 `inputs` 上下文这一条无法离线证实，而这类表达式一旦不被接受是整个 workflow 失效（本仓 cookbook 有「表达式让 GitHub 解析失败」的先例）；且自签只被 mac/win 两腿消费，提层会让 linux 腿平白多一个 `SELF_SIGN=0`。保持 job 级。
- **用 `$/.github/actions/package-setup` 自仓语法替代 `./`**（R2 给的第二个修法）：落败——该语法不需要 checkout（runner ≥ 2.336.0），但与在本仓零先例的新语法换掉 9 行相比，发布链路上「已知能用」优先；本批不引入它，留作后续独立项。
- **被调用方完全不声明 `concurrency`**（社区文档的建议姿势）：落败——会丢掉「同 ref 重 dispatch 自取消」的既有便利（重复预览要跑满两轮三平台构建）；改成三段式组名即可两全，代价是两条表达式。
- **保留跨 run API 下载与 `release.yml` 的整轮重发恢复**：落败——`needs:` 已结构性保证「包腿红则不发」，跨 run 下载要 2 次 API + 解压且依赖 `head_sha` 关联，而「Re-run failed jobs」恢复更短且复用同 run 产物。
- **恢复 freshness 定时巡检（`schedule`）**：落败——被巡检对象（`check-pin-freshness.sh` 的 dsh/node 钉版与闭包一致性）已随 online-first 整体退役，脚本与钉版都不在仓库里，无对象可巡；`freshness.yml` 的退役成立，本批以本 ADR 具名记录，不再补 schedule。
- **`self_sign` 保持 `github.event.inputs` 原样**：不可行——`workflow_call` 形态下该上下文为空，旋钮会从「恒不生效」变成「不存在」；既然必须改，就按脚本的门改对（`&& '1' || '0'`），不写一个已知是死的映射。

## Consequences

- 三平台打包改一次只改一处；包腿与发布作业同 run，发布作业不再空转轮询（省掉 run `36325106379` 那 6m43s 的 runner 占用——注意这**不是端到端墙钟收益**，包腿本就是关键路径，省的是发布作业的占用与跨 run 下载）。
- 「包腿红仍出 Release」从「靠手写逃生检查兜」变成「结构性不可能」（`needs:` 不放行）。
- 自签旋钮从死变活：dispatch `self_sign=true` → `SELF_SIGN=1` → `codesign`/`signtool` 自签路径真执行（缺工具仍 fail loud）；tag 发布路径的未签名行为不变。
- 新执行点：`bash scripts/package-version.sh --self-test` 进 `ci.yml` 的 `docs` job；判据矩阵登记在 `docs/testing.md`。
- 预览重 dispatch 仍自取消（`package-workflow_dispatch-<ref>` 组内）；tag 发布轮不被任何 dispatch 影响。
- **残余验证缺口**：①**tag 触发臂到不了**——`workflow_dispatch` 用的是**该 ref 所在提交的** workflow 文件，tag 指向的提交里没有本批的新文件，故「tag ref 上版本门放行/拦截 + 发布作业的 download-artifact→preflight→Release」只能由下一次真 tag 关闭（与 [CI 门禁诚实化](2026-09-28-ci-gate-honesty.md) 同款缺口）；本批的机器替代证据 = `--self-test`（11/11，覆盖 tag 门全部分支与参数缺值诊断），分支 ref 上的 `release.yml`/`package.yml` dispatch（覆盖 reusable 调用面、`needs:` 跳过语义、三条腿）待本批推送后补跑，实跑记录见 Testing。②Windows 腿的自签路径（`SELF_SIGN=1` → `signtool`）只能由人工带 `self_sign=true` 的 dispatch 关闭，本批未跑（改的是映射，不是签名实现）。③本 workflow 的 `concurrency` 组名解析与自取消语义（`cancel-in-progress` 的实际生效面）只有真 runner 能实证——本批的组名设计与死锁规避是文档+推演结论（runner 侧无本地可跑的验证面）。
- 后续若要给预览再加「同一分支只保留最新一轮」之外的并发语义，改的是 `package.yml` 的 `concurrency` 两行，别动发布轮的语义。

## Testing

- `bash scripts/package-version.sh --self-test`：11/11 断言通过（tag 三形态 + 两种不一致 + 分支输入优先 + 分支回退 + 无 `<Version>` + 分支名像 tag + csproj 缺失 + 参数缺值须有诊断）；真实仓库路径手验三条：分支 ref 回退 `0.5.8`（真 csproj）、`refs/tags/v9.9.9` 取 tag、输入 `v9.9.8` 不一致 → `::error::` 点名两版本并 exit 1。
- 四个 workflow + 一个 composite action 全部 `yaml.safe_load` 解析通过；`python3 scripts/verify-governance.py` exit 0 —— 该门的扫描面本批已扩到 `.github/actions/**/action.y*ml`（含 `runs.steps` 下潜；后缀与层级都收，将来新增嵌套 action 不会静默落在门外），并以注入法反向验证：在 composite 的 `run:` 里临时插入 `${{ env.PROBE }}` 即判红（`step「解析打包版本…」`）、还原后转绿；`--self-test` 新增两条 composite 夹具通过。
- **旧文件名在 `implemented/**` 语料里的勘误定档**：本批删除三份 `package-*.yml` 后全库仍有引用，判据分开——**历史叙述不改**（Testing 段的 `dispatch package-*` + run id、逐批实录、Problem 段对当时状态的描述：那些记录的当时文件名是对的）；**把已删文件当作现行落点的断言**（今天按该文件名 grep 会落空）按勘误纪律补 `Erratum:` 行、正文不动，共 **16 处**（`git diff --cached -- .agents/notes/ | grep -c '^+Erratum:'` 可复核）：`2026-09-28-ci-gate-honesty`、`2026-08-28-publish-aot-jit-alignment`、`2026-09-27-cookbook-retired-entry-cold-archive`、`2026-08-20-free-self-sign-dev`、`2026-08-20-community-targeted-testing`、`2026-08-20-remove-linux-main-push-packaging-trigger`、`2026-09-13-payload-smoke-probe`、`2026-08-20-linux-packaging-pilot-harness-model`、`2026-09-25-smoke-runner-deepening`、`2026-09-13-workflow-input-env-interpolation`、`2026-09-26-smoke-linux-xvfb-fullchain`、`2026-09-26-smoke-witness-real-and-eval-first-hop`、`2026-09-27-macos-cookie-grace-reload-and-witness-gate`、`2026-08-20-drop-standalone-zip-artifacts`、`2026-08-20-unified-release-and-structured-notes`、`2026-09-26-page-verdict-gate`（末篇同时点出文件名与落定窗数值两条失效面）。
- 三腿**行为保真**由 R2 逐 job 逐字比对确认（与 base 三份旧 workflow 的 step 体结构级相等）：六处 `if: always()`、九处 `upload-artifact`（6 证据 + 3 包）、六处分档表达式、`fail-fast: false`、matrix/`runs-on` 全部逐字保住；版本解析判据由 linux 单腿扩到三腿属收紧而非丢失。
- 门禁全绿：`verify-adr-format` / `verify-cookbook` / `verify-doc-budgets` / `verify-md-links` / `verify-readme-badges` / `verify-handoff-structure` / `verify-governance` / `verify-skill-format` / `verify-code-health --enforce` / `verify-code-conventions --enforce` / `verify-ui-copy`。
- **dispatch 实跑**（`.github/workflows/**` 变更的硬要求，见 feature-flow 步骤 3）：本批推送后在分支 ref 上实跑三条——`release.yml`（三平台包腿 + `needs:` 跳过发布作业）、`package.yml` 直调（预览路径）、`ci.yml` 推送轮（新增自测步）；run id 与 verdict 随本批回写至本行。

## Related

- [CI 门禁诚实化](2026-09-28-ci-gate-honesty.md)：本批承接其 tag/输入版本门遗留的残余验证缺口，并把判据搬进可自测脚本；该篇已按勘误纪律补 `Erratum:` 行（三份 package workflow 的文件名与残余缺口陈述随本批更新）。
- [产物校验链](2026-08-24-artifact-verification-chain.md)：`release-preflight.sh` 资产矩阵与 `verify-package-layout.sh` 的判据家——本批不动资产名与矩阵。
- [统一发布与结构化正文](2026-08-20-unified-release-and-structured-notes.md)：单一 release owner 的决定家；本批只改它的产物获取方式（跨 run API → 同 run `needs:`）。
- [免费自签（开发用）](2026-08-20-free-self-sign-dev.md)：`SELF_SIGN=1` 门与自签实现的决定家；本批把 CI 侧该门的输入映射接正。
- [Linux 打包参照 pilot-harness 模型](2026-08-20-linux-packaging-pilot-harness-model.md)：`fail-fast: false` 与 rpm 显式 Requires 的出处。
