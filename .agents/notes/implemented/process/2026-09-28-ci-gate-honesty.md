# Agent Note: CI 门禁诚实化——每道门都必须有能失败的路径

Status: implemented

Erratum: 2026-09-28 — 本笔记所述三份 `package-{linux,macos,windows}.yml` 已随「CI 打包三流合一」合并为单一 `package.yml`（共用件在 `.github/actions/package-setup`）；其中 tag/输入版本一致性判据移入 `scripts/package-version.sh`（带 `--self-test`，覆盖 tag 门全部分支与参数缺值诊断），本笔记「残余验证缺口」那条的**判据面**随之关闭（判据已搬出 tag-only 的 `if:` 外壳，成为 `ci.yml` 每次实跑的机器断言）；tag ref 上的整合证据仍待下一次真 tag（见 unification 的残余缺口①）；「遗留缺口②」的 `github.event.inputs.self_sign` 同批接正为主题面的 `inputs.self_sign && '1' || '0'`（该旋钮此前恒不生效）。见 [ci-package-workflow-unification](2026-09-28-ci-package-workflow-unification.md)。正文不动。

Review: FULL/2026-09-28/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

CI 全绿，但绿得没有语义：一批被当成「门」的步骤没有任何非零出口。逐条清点（本批的判据清单）：

| # | 位置 | 形态 | 后果 |
|---|---|---|---|
| 1 | `package-linux.yml` 的「校验产物」 | 三条命令全带 `\|\| true`，既不看退出码也不看内容 | Linux 腿唯一的产物内容检查是空的（win/mac 腿有 `verify-package-layout.sh`，Linux 没有） |
| 2 | `package-windows.yml` 的 WebView2 注册表探针 | `$error.Clear(); exit 0`，恒 0 且混在 gate 区 | 读起来像一道门，实际是留痕 |
| 3 | `governance.yml` 整个 job | 内联 bash 只有 echo / `::warning::`，无 exit 非 0 路径 | 一道不可能失败的门；且与 `verify-governance.py` 语义分叉（脚本 docstring 自称「与 CI 同逻辑」） |
| 4 | 6 处冒烟截图/日志上传 | 恒 `if-no-files-found: warn` | `if: always()` 下 warn 永远只是提示：冒烟红了、证据也没了，仍然绿 |
| 5 | `release.yml` 的「生成合并 SHA256SUMS」 | 同 run 内先算后列，下游 `release-preflight.sh` 的 `sha256sum -c` 复核同一批字节 | 自指：写成什么样都会被自己复述一遍 |
| 6 | `ci.yml` 的档位门 fallback | 无 base 时退到「工作树扫描」，而 CI 的 checkout 是干净的 | 空变更集恒绿——一道不会失败的档位门 |

另一类是「该在而没在」：

- `ci.yml` 的 `code` paths filter 漏 `.editorconfig` / `Directory.Build.props` / `Directory.Packages.props` / `global.json`：纯 `.editorconfig` 改动让 `build-test` 整体跳过，**连 format 门禁一起消失**（`pre-push` 早已按同款理由把 `.editorconfig` 纳入，CI 侧没对齐）。
- `coverage summary` 只打印不比对：基线只被徽章相等门禁互校，**覆盖率的真实下滑无人发现**（「徽章 ↔ 基线」自指闭环）。
- `verify-skill-format.py` 在 `ci.yml`/`pre-commit`/`pre-push` 三处执行点全无；`verify-review-brief.py` 连本地 hook 也没有。
- `package-linux.yml` 的 tag/输入版本一致性只打 `::warning::` 继续走 tag：手工 dispatch 填错版本会被静默吞掉——这是发布正确性，不是提示。

共性成因：这些步骤都写成「取证型」（日志好看、失败不拦）。一旦被读成门，后续改动就以它们为绿依据，于是绿与健康脱钩。

## Decision

按三类判据改，不留「既不像门也不像取证」的中间态：

**一、死门二选一：变真断言，或改名成显式「不判门」的取证。**

- Linux「校验产物」拆成两步：「包内容布局断言」（真门，调 `verify-package-layout.sh --target <staging>/usr/lib/deepseek-harness-desktop`，与 win/mac 腿同判据）+「包可解析断言 + 打包取证」（`dpkg-deb -I`、`rpm -qp --requires` 的解析成功是不带管道的真断言，内容清单仍带 `|| true` 留痕——`| head` 下生产者收 SIGPIPE 会把留痕行变成红）。
- Windows 注册表探针：步骤「冒烟前置（Defender 排除 + WebView2 先探后装）」（落点 `scripts/prepare-windows-smoke.sh`）——`pv` 探针只决定装不装，安装器退出码是判门者，探针只留痕。
- `governance.yml`：第一步跑 `python3 scripts/verify-governance.py`（退出码即结论，与 `pre-commit` 同一份实现，语义分叉随之消失）；正文形状检查降为显式的「提示：…（不判门，仅日志留痕）」步。
- 冒烟证据上传：`if-no-files-found` 按冒烟步 outcome 分档——`${{ steps.smoke.outcome == 'failure' && 'error' || 'warn' }}`。冒烟跑过且失败时证据缺失判 `error`（尸检材料丢了）；其余情形（冒烟绿，或被前置步失败跳过）判 `warn`——跳过时真因在别处，不该再添一条无证据的红来掩埋它。
- `release.yml` 的 SHA256SUMS 生成步：步骤名注明「准备，非校验门」；错包检测的家是 `release-preflight.sh` 的资产矩阵 + 体积下限。
- `ci.yml` 档位门 fallback：无 base 即 fail loud（同 `verify-review-tier` 对不可达 base 的既有处置「无法判定变更集 = 违规」），不得静默放行。

**二、补门与补执行点：一道判据只留一份实现。**

- `ci.yml` 的 `code` filter 补上四个结构级构建输入（`.editorconfig`、`Directory.Build.props`、`Directory.Packages.props`、`global.json`）。
- `coverage summary` 步带 `--baseline scripts/test-baseline.json --tolerance-pp 0.5`：实测低于基线超过 0.5 个百分点即 exit 3 判红；基线文件缺失/形状不符 exit 4 fail loud（绝不读成「无下限、通过」）。比对只在 `test with coverage` 步成功时进行（该步加 `id: test`）：测试红了只会留下半量 TestResults，拿它比基线是把「测试失败」误诊成「覆盖率下滑」。
- `verify-skill-format.py` 进 `ci.yml` 的 `docs` job；`verify-review-brief.py` 进 `pre-commit`（`.review-briefs/*.md` 存在即 `--enforce`）——简报是 gitignore 的本地工作文档，CI 永远看不见它，本地由「评审启动前手工 `--enforce`」与 pre-commit 两处承担。
- `package-linux.yml` 的 tag/输入版本不一致改 fail loud。
- 每道门跑在哪一档（`ci.yml docs` / `pre-commit` / `pre-push`）的矩阵，单一事实源 = `docs/testing.md`。

**三、判据精度与判据强度分开：** 容差进判据（覆盖率 0.5pp），不进强度（低于下限就是红）。

## Alternatives considered

- **冒烟证据上传一律 `error`（方案原动作）**：落败——Windows 腿的「安装链兜底 PASS」在无桌面会话时本就不产截图（`smoke_shot` 无 `DISPLAY` 静默跳过），纯 `error` 会把一个设计认可的绿判成红。用一个随机红换掉一个假门，不是诚实化。
- **整删 Linux「校验产物」步（方案的另一选项）**：落败——Linux 腿当时没有任何包内容断言，删了等于把「内容对不对」整块交给无人；换成真断言是净增判别力，代价只是一次 dispatch 验证。
- **把 rpm Requires 假依赖（`musl`/`aarch64`/`perl`）改成机械断言**：本批落败——该不变量在文档里是注释级描述、没有既有实测基线，硬编断言可能把合法包判红，而风险面是 tag 发布链路。本批只把它标成留痕并记为遗留缺口。
- **整删 `governance.yml` job、只留脚本调用点**：落败——Issue/PR 正文形状提示对分诊有用（模板字段的真门由脚本承担），删 job 会把提示一起丢掉；让 job 调脚本消除的是语义分叉，不是提示。
- **把 `verify-review-brief.py` 降级标为「人工纪律」（方案的另一选项）**：落败——简报是评审任务有界的唯一机械保障，且它要的「工作树只有暂存项」正好是 `pre-commit` 时刻的状态，执行点现成。
- **容差判据另起 `verify-coverage.py`**：落败——并集复算与基线比对读的是同一份 cobertura 与同一份 JSON，拆开要重算一次并集或传中间态；`coverage-summary.py` 是这条数据现成的唯一家。
- **只按「与基线精确比对」判覆盖率、不设容差**：落败——逐轮漂移可差数行（本批本机复算 59.89% vs CI 基线 59.95%，n=1【探索性】；2026-09-12 本机 vs CI 差 4 行【因果定性属推断 · 未证】），精确比对会把 CI 变成随机红；0.5pp 比这些观测大一个量级【探索性，取值理由见下】，真实下滑照样抓得住。
- **`pre-push` 补齐缺失的门（方案标 Minor）**：本批不做——只补「三处全无执行点」的两道；`pre-push` 侧未跑的门已在 `docs/testing.md` 的执行点矩阵逐格写明（该档只跑三道文档门禁 + 档位门），而不是让它看起来跑了。

## Consequences

- 从此有几处会真的红：Linux 包内容布局不符、deb/rpm 不可解析、冒烟跑过且失败而证据丢失、覆盖率低于基线超容差、tag 与输入版本不一致、档位门禁无法判定变更集、`.editorconfig` 类结构改动漏跑 build-test。
- 终局是：冒烟跑过且失败时证据缺失判 `error`；冒烟绿判 `warn`。**回归方向是静默降回 warn**——`if-no-files-found: ${{ steps.smoke.outcome == 'failure' && 'error' || 'warn' }}` 里改掉或删掉 `id: smoke` 会让 outcome 为空、表达式取到 `'warn'`，即无声退回变更前那道永不失败的门（不是变红）。改这三个工作流的冒烟步时须同改 id。
- 覆盖率真实下滑 >0.5pp 时的动作路径写进 `docs/testing.md`：补测试，或按「跟值」流程在同一变更里更新 `scripts/test-baseline.json` 与 README 双语徽章（三者同值由 `verify-readme-badges.py` 强制）。
- 覆盖率比对挂在 `steps.test.outcome == 'success'` 上：测试红了只复算不判门，避免把「测试失败」报成「覆盖率下滑」（代价：测试红的那一轮不再有基线下限信号——但那一轮的红本就来自测试；连 cobertura 都没留下时该步仍按缺产物判红）。
- `pre-commit` 第 12 步的冻结判据是整树级的（`git status --porcelain` 只允许暂存项），不按本批路径收敛——这是 `review-freeze-worktree-discipline` 的判据本体，散落的未跟踪文件本就该拦。
- **遗留缺口（已识别、本批不做）**：①rpm Requires 假依赖不变量仍无机械断言（留痕 + 人眼复核）；②`package-windows.yml`/`package-macos.yml` 仍用 `github.event.inputs.self_sign` 取 boolean 输入（新形态为 `inputs.self_sign`）——改它会动发布链路的自签行为，留给发布链路的独立批次；③`pre-push` 的 `scripts/change-scope.sh || true` 保留：它是取证型工具不是判据，去掉 `|| true` 会让「无 base」这类正常情形拦住 push；④`coverage-summary.py` 与 `verify-readme-badges.py` 都解析 `scripts/test-baseline.json`——本批把前者的 `coverage` 形状判据收紧到与后者同一正则，但前者在文件级仍更宽（不重复后者的「键集恰为两键」「重复键违规」）；方向安全，本家独认的文件会被同一 `docs` job / `pre-commit` 里的后者判红，共用助手留待脚本规范批。

## Testing

- `python3 scripts/coverage-summary.py --self-test`：18 条断言全绿。新增 11 条：5 条基线下限分支（高于基线通过 / 低于基线 0.47pp 但在容差内通过 / 恰在下限通过 / 低于下限 0.01pp exit 3 / 下限按基线两位精度取整——最后一条是唯一能把「取整」与「原始浮点比较」分开的样例：基线 83.834 原始下限 83.334 会判红、取整下限 83.33 通过）、5 条基线形状夹具（`<rate>%` 解析、非百分比、非字符串、缺 `coverage` 键、JSON 损坏）、1 条 `main()` 的 exit 4（基线不可读不得读成「无下限、通过」）。
- 真实 artifact 复算（本仓 `TestResults/` 三份 cobertura，本机 `-c Debug`，n=1）：`covered=5394 valid=9007 line-rate=59.89%`，对 CI 基线 `59.95%` 差 0.06pp → 容差内通过【探索性；单次本机复算 vs CI 基线，属环境差】。同一批文件在精确比对下判红，是「容差必需」的当场示例，但**不足以定论漂移分布**——容差取值的依据是「基线记两位小数、逐轮有漂移」这一可复现观测与《testing.md》已记的 2026-09-12 本机 vs CI 差 4 行【该因果定性属推断 · 未证】，不建立在本条 n=1 上。

- 六个工作流 `yaml.safe_load` 全解析通过（新增 step 名里的 ASCII `:` 被本地治理门禁当场抓出，已加引号）；`python3 scripts/verify-governance.py` exit 0、`--self-test` 通过。
- 门禁全绿：`verify-adr-format` / `verify-cookbook` / `verify-doc-budgets` / `verify-md-links` / `verify-readme-badges` / `verify-handoff-structure` / `verify-governance` / `verify-skill-format` / `verify-code-health --enforce` / `verify-code-conventions --enforce` / `verify-ui-copy`。
- **dispatch 实跑**（`.github/workflows/**` 变更的硬要求，见 feature-flow 步骤 3）：
  - `ci`（PR #2 `pull_request` 路径）`36337067299` 全绿：`changes` 命中 `scripts/**`；`docs` job 实跑新增的 `verify-skill-format.py`（`OK: 8 skills conform`）并以 PR `base.sha` 走档位门 `--since`；`build-test (ubuntu)` 的 coverage 步按 `TESTS_OUTCOME: success` 走带基线分支，打印 `line-rate=59.95%`（恰等基线，容差内）。
  - `governance`（同 PR）`36337067272` success：新增「治理门禁」步（`verify-governance.py` 输出 `OK`）与「提示（不判门）」步各跑一次——issue/PR 事件是它唯一的触发路径，无法 dispatch，故用 PR 事件取证。
  - `package-linux` dispatch `36337109586` 两腿全绿：布局断言在真 staging 上通过（`ok: dsh-desktop-companion.tgz (16K)`），`dpkg-deb -I` / `rpm -qp --requires` 断言通过，留痕显示 rpm Requires 恰为 `libwebkitgtk-6.0.so.4()(64bit)` + `libadwaita-1.so.0()(64bit)`（无 `aarch64`/`musl`/`perl` 假依赖），四个证据 artifact 均落地（截图 75KB / 日志 9.2KB）。
  - `package-macos` dispatch `36337114236` 两腿全绿；`package-windows` dispatch `36337111841` 全绿（含改名后的 WebView2 步；截图 130KB / 日志 7.2KB 均上传）——绿跑下证据位有实体，分档表达式没有把设计认可的绿判红。
  - **残余验证缺口**：tag 触发的版本门（`if: startsWith(github.ref, 'refs/tags/v')`）dispatch 到不了。该段 shell 已在本地按四种输入形态实跑：tag `v0.5.9` 配输入 `v0.5.9` / `0.5.9` / 留空均 exit 0，配输入 `v0.5.8` exit 1 且 `::error::` 点名两个版本号——下一次真 tag 会给出真 runner 证据。`release.yml` 的改动只有步骤名与注释（零表达式、零新增步），未 dispatch（dispatch 它会在真仓库创建 Release）。

## Related

- [评审档判定机械强制](2026-09-03-review-tier-escape-proofing.md)：`ci.yml` 档位门禁的家；本批只改其无 base 分支的处置。
- [评审证据新鲜度门禁](2026-09-13-review-evidence-freshness-gate.md)：「无法判定变更集 = 违规」的既有判据，fail loud 与之同源。
- [评审对象冻结纪律](2026-09-13-review-freeze-worktree-discipline.md)：`verify-review-brief.py` 的判据家；本批给它补 `pre-commit` 执行点。
- [覆盖率基线取 CI cobertura 实测](../testing/2026-09-12-coverage-baseline-from-ci-cobertura.md)：基线值来源与跟值义务。
- [覆盖率基线多工程并集](../testing/2026-09-14-coverage-baseline-multi-project-merge.md)：`coverage-summary.py` 的合并规则家；本批在同一脚本里加基线比对。
- [Linux 打包参照 pilot-harness 模型](2026-08-20-linux-packaging-pilot-harness-model.md)：rpm `AutoReqProv:no` + 显式 `Requires` 的决策家（遗留缺口①的不变量出处）。
- [产物校验链](2026-08-24-artifact-verification-chain.md)：`verify-package-layout.sh` 与 `release-preflight.sh` 的判据家。
