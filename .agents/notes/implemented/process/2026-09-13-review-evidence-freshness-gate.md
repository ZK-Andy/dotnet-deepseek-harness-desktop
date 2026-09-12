# Agent Note: 评审证据须本批新产（机器判据）

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

[review-tier-escape-proofing](../../implemented/process/2026-09-03-review-tier-escape-proofing.md) 把评审档判定机械化，但证据判定留了一个可绕的口子：`scripts/verify-review-tier.py` 的 `_evidence_in_change` 只要发现「变更集内**任一** implemented ADR 的头部带合法 `Review:` 行」即认为证据齐备，只校验日期是合法日历日，不做任何窗口比较。

于是同日有多次 FULL 变更时，新变更只要在变更集里**顺带改动**一个旧 implemented ADR（例如按评审建议同步其「已知边界」段），该 ADR 自带的旧 `Review:` 行就替新变更顶了包：2026-09-12 批次 `--staged` 判定 OK，而该批的 Review 证据尚未产出。脚本头注释与 owning ADR 都写着「证据须与触发变更同批」——该义务由本篇的机器判据承载。

同一判定在 2026-09-13 的 R2 评审里又被实测出三条同源假绿：**改名搬运**——笔记改名且正文重写超过一半时 git 的改名检测落空，新路径整文件记为新增，被继承的旧 `Review:` 行因此算「本批新产」（`D old.md` / `A renamed.md` + FULL 变更 → `--staged --enforce` exit 0）；**改标题的改名**——即使 git 检测到改名，`git diff -U0 -- <新路径>` 的 pathspec 让目的地显示为整文件新增，只按笔记标题排除时把标题一并改掉即再度 exit 0；**基准不可达**——`--since <base>` 的 base 不存在时 `git diff` 报 128、stderr 被吞、变更集读成空集，打印 `review-tier: OK` exit 0，FULL 硬闸整条失效。三者都不是「证据真假」问题，而是判定在无法成立时静默放行。

另有一条**规则从未生效**：`.github/workflows/**` 的 FULL 触发写成 `".github/workflows" in p.parts`——多段字符串永不等于单个路径分量，判定恒假，与脚本 docstring 和 owning ADR 的声明相反（`.github/workflows/ci.yml` 实测 `_classify` 返回非 FULL），工作流变更一直只靠其它触发偶然覆盖。

## Decision

证据判定收窄为「`Review:` 行必须是**本批新增行**」，落在 `scripts/verify-review-tier.py`。同批评审另暴露三条同源假绿/假红面与一条死触发面，一并收口（同一决定：**门禁在无法判定时不得静默放行**）：

- 新增 `_added_lines_for(rel, repo, staged_only, since)`：按所选 git 时刻取该路径的新增行文本（`git diff -U0` 的 `+` 行，排除 `+++` 文件头；工作树模式的 `git diff HEAD` 已同时覆盖暂存与未暂存，未跟踪文件视为整文件新增）。git 无法产出 diff 时返回 `None`，调用方 fail loud，不读成「没有新增」。
- `_evidence_in_change` 在原有校验（路径在变更集内、`Status: implemented`、`Review:` 行严格匹配 `FULL/<date>/R1=ok R2=ok R3=ok`）之上，要求该 `Review:` 行文本命中本批新增行集合；未命中即不算证据，报错文案点明「证据须本批新产」。判定的求解顺序是先看新增行、命中后才去取继承集——最常见的「旧 ADR 被顺带改动、`Review:` 行非新增」形态因此不付继承集枚举成本。
- **继承行不算新产**（`_inherited_review_lines`）：本批删除或改名离开的笔记，其在 base 版本里的 `Review:` 行进入排除集，用**两个键**：`(笔记标题, 行)` 与 `(改名目的路径, 行)`。前者兜住「正文重写超过一半、git 改名检测落空」的形态（不加该键时 `D old.md / A renamed.md` + FULL 变更 → `--staged --enforce` exit 0）；后者兜住「git 检测到改名、但笔记标题被一并改掉」的形态——`git diff -U0 -- <新路径>` 的 pathspec 会把目的地显示为整文件新增，标题键那时已失效（不加该键时 exit 0）。按标题键而非按行文本全库排除，避免误伤同日评审的同族笔记（其 `Review:` 行文本必然逐字相同）。该枚举无法判定时返回 `None` → fail loud，不把「不知道删了什么」当「没删」。
- **无法判定变更集 = 违规**（`_changed_paths_or_error`）：`--since <base>` 的 base 不可达（force-push 前的 before 哈希、rebase 后消失的 `base.sha`、浅克隆）或 git 调用失败即报 `cannot determine the change set`，`--enforce` exit 1——空集不得被读成「无 FULL 触发」。`_repo_changed_paths` 保留列表形态作为 `verify-review-brief.py` 的消费契约（该消费者在不可判定时降级为 LIGHT 档，拦下由档位门禁自身承担）。
- **`--staged` 读 index 而非磁盘**（`_note_text`）：从 `git show :<path>` 读即将提交的树——证据 ADR 已入 index、工作树副本被删（部分提交流）时判据看的正是将入库内容。
- **头区而非头 15 行**（`_header_zone`）：`Status:`/`Review:` 的判定窗口 = 第一个 `## ` 小节标题之前（上限 60 行）——固定 15 行会把长头区里的合法 `Review:` 行读成不存在，报错文案也会误指「不是新增」。
- **`.github/workflows/**` 触发按仓库相对路径前缀判定**：`rel.startswith(".github/workflows/")`——工作流变更由行为契约面触发 FULL（此前的 `p.parts` 谓词判定恒假，见 Problem）。
- 正当形态照常放行：新立 ADR（整文件新增）、proposed→implemented 迁移（新路径整文件新增，`Review:` 行随正文）、给既有 implemented ADR 新加或改写 `Review:` 行。
- owning ADR 的「已知边界」段指向本判据（边界由机器判据承载）；`scripts/verify-review-tier.py` 头注释写当前判定语义。

## Alternatives considered

- **断言证据 ADR 必须是本批新建文件（git status `A`）**：落败——同日折叠（proposed 直接写为 implemented）、以及给既有 ADR 补/改 `Review:` 行的正当形态都会被误拦；「新增行」判据覆盖这三种形态且更贴「证据本批新产」的语义。
- **比较 `Review:` 行日期与变更窗口**：落败——日期由人写，同日多次变更本就同日期，分不出新旧；靠提交时间或 runner 时钟同样不可靠（时区/时钟偏差），且本仓已有同类环境差踩坑。
- **机器验评审报告真伪（要求完整报告文件）**：落败——已在 owning ADR 的 Alternatives 判落败（机器难验真伪、易被空壳文件糊弄），不重开该判据。
- **维持现状、只靠文字与评审代理自觉**：落败——2026-09-12 已实测绕过一次，与 owning ADR 记录的 E1 逃逸同源：写明规则不构成限制。
- **按 `git diff --name-status` 的 A/R 状态区分（改名源不计新增）**：落败——改名是否被检测出取决于正文重写比例，判据会随 git 的相似度阈值漂移；且 proposed→implemented 的正常迁移本身就是一次改名，按状态判会把正当形态与「搬运证据」混为一类。
- **对「本批删除路径的旧 `Review:` 行文本」做无标题指纹排除**：落败——`Review:` 行是公式化文本，同日评审的任意两篇逐字相同，归档旧笔记与新建证据同批时会误拦正当形态；按笔记标题限定才把「同一篇被搬运」与「同日同族不同篇」分开。
- **只按笔记标题（或只按改名目的路径）建排除集**：落败——两个键各堵一半：只按标题，改名+改标题的搬运行落空；只按路径，正文重写让 git 改名检测落空时没有路径信号可用。两键并用才覆盖两种已实测形态；两个键的成员都取自 base 版本旧笔记，比对的是**行文本**（改名目的地携带新的 `Review:` 行照常放行，见测试夹具 18）。
- **base 不可达时回退到工作树/全仓扫描**：落败——回退把「不知道比什么」换成「按另一种口径比」，静默换判据比拦下更难审计；base 缺失属配置面（CI 的 `fetch-depth`），应显式暴露。
- **删掉失效的 `.github/workflows/**` 触发（承认它从未生效）**：落败——工作流是行为契约面（打包、发版、CI 判定），脚本 docstring 与 owning ADR 一直按 FULL 对待；删除等于把「规则写错」固化成「规则不存在」，而修复只是一个谓词。

## Consequences

- 收益：「证据随变更」是机器判据而非文字义务；同日多批 FULL 变更各自需要自己的证据，改名搬运（含改标题形态）与不可达 base 两条绕过路径关闭，工作流变更由 `behavior-surface` 触发 FULL。
- 代价：证据必须在本批 diff 里出现 `Review:` 行的增行——若某批的评审结论只落在别处（例如仅改动既有 ADR 的正文、不动 `Review:` 行），门禁拦下，须在本批 ADR 内落该行。
- 代价：base 不可达即拦下——CI 的 `docs` job 需 `fetch-depth: 0` 才能解析 base；无法判定变更集的推送会被挡，方向是拦住而非放过。
- 代价：`.github/workflows/**` 变更由 `behavior-surface` 触发 FULL，纯工作流批次也要带三审证据。
- 判定仍不覆盖「本批跑了评审但证据 ADR 与 diff 分离」的情形（`--since` 视 base..HEAD 为一批）；跨批归属仍是执行者的动作面。
- 判定仍不防**伪造**证据（手写一行 `Review:` 进新 ADR 与真实评审在机器看来同形）——该边界在 owning ADR 的 Alternatives 已定，本条收窄只针对「搬运既有证据」。同一逻辑的残余：正文重写超过一半**并且**标题与路径一并改掉时，三个信号（新增行、标题键、路径键）全部失效，落在伪造类。
- 边界：改名目的地携带的 `Review:` 行若与旧笔记 base 版本**逐字相同**（公式化行只在日期上区分，故等价于同一批评审日），判据读成继承并拦下——文本上不可区分「同日新产」与「搬运」。补救：该批由另一篇 ADR（非改名目的地）承载证据行，本仓常规批次都另立 ADR。

## Testing

`python3 scripts/verify-review-tier.py --self-test`：18 例夹具全绿——相对基线新增八例：「同批新产证据放行」/「改名重写继承行不顶包」/「改标题的改名不顶包」/「改名目的地携带新 `Review:` 行照常放行」/「不可达 base 判违规」/「`--staged` 从 index 取证据」/「未跟踪 ADR 视为整文件新增」/「工作流路径判 FULL」。真实仓库与临时夹具复验：本批变更集里三篇既有 implemented ADR（2026-08-31 / 2026-09-03 / 2026-09-12）本批新增的**严格证据行**（`FULL/<date>/R1=ok R2=ok R3=ok`）数均为 0（其中一篇本批改了正文但没动证据行）——不带新鲜度判据时该变更集放行，带判据则判 FULL 且 `--enforce` exit 1；`D old.md` / `A renamed.md` + FULL 变更：不带排除集时 `--staged --enforce` exit 0，带排除集 exit 1；改名+改标题 + FULL 变更：不带路径键 exit 0，带路径键 exit 1；同一改名目的地改带**新日期**的行则 exit 0（排除集按行文本比对，非按路径）；`--since` 的 base 不可达时判违规（`--enforce` exit 1，报告模式 exit 0）；`ci.yml` 由 `behavior-surface` 触发 FULL（该谓词判定恒假时 `_classify` 返回非 FULL）。

## Related

- [review-tier-escape-proofing](../../implemented/process/2026-09-03-review-tier-escape-proofing.md)：本判据的 owning ADR，其「已知边界」段由本篇收口。
- [review-scope-narrowing](../../implemented/process/2026-08-31-review-scope-narrowing.md)：触发枚举与简报表（`verify-review-brief.py` 复用本脚本的档位判定）。
