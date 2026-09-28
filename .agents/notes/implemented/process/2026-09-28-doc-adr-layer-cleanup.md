# Agent Note: doc-adr-layer-cleanup（E 批：ADR 层裁定执行 + 文档层清理）

Status: implemented

Review: FULL/2026-09-28/R1=ok R2=ok R3=ok

三审收口（0 Blocker；Suggestion 16 条——R1 5 / R2 6 / R3 5，其中两路重复 1 条——逐条裁定：采纳 14 / 部分采纳 1 / 记账 1，修复一次收口进本批）：R1 5 条（--facts docstring 与实现对齐×2、退出码语义、argparse 显式化、注释瘦身为指针）；R2 6 条（md-links 排除面第三类入 docstring 唯一家、testing.md 门禁表补 compose-root、architecture.md `Classify`→`ClassifyDetail` 符号漂移、NSIS 回退过时陈述×2、--facts 去重 + corpus 跳过集对齐）；R3 5 条（spawn-unified run id 降级、吸收声明补指针、归档例外措辞加宽、bootstrap-window-ready-wait 弱判据记账、§7 节锚降级）。

Related: 总纲 [proposed/process/2026-09-27-post-packaging-churn-restructure](../../proposed/process/2026-09-27-post-packaging-churn-restructure.md)（本批为其 §7 的执行）；各裁定对象散见 `implemented/` 与 `archived/`。

## Problem

近两日震荡期 31 篇新 ADR 约 2/3 是会话 changelog 而非 durable 决策：单条决策被切成 4–6 篇同题材笔记、CI run id 占正文骨架（68 处）、自我证伪的笔记仍挂 `implemented/` 无 Erratum。文档层同病：质量门清单 4 处多家、架构/启动模型描述代理层之前的形态（B3）、8/16 条归档链接死链而链接门禁整目录排除、durable 文档 8 处变更史 slop、`architecture.md`/`testing.md`/`development.md` 无预算条目。门禁只查形状不查语义（「文档陈述 == 代码事实」零覆盖）。

## Decision

按总纲 E 批（文档与 ADR 层清理）范围执行，裁定来自用户已审核的方案原文（本地未提交文档；下列小节锚仅助读，裁定内容已全文内联）：

- **ADR 裁定（§7.1）**：归档 10 篇（`shell-mint-and-forward`、`macos-cookie-grace-reload-and-witness-gate`、`smoke-witness-real-and-eval-first-hop`、`auth-replay-diagnostic-and-mac-ci-hygiene`、`smoke-sw-render-probe`、`bootstrap-provisioning-observability`、`webauth-token-reentry`、`bootstrap-window-ready-wait`、`navigate-call-timeout`、`npm-global-bin-path`，只插 `Archived:` 行）；合并删 5 篇——`upgrade-tunnel-watcher-exemption`→host-authority、`windows-bootstrap-dsh-path-and-stall-watchdog`→windows-dsh-spawn-unified、`smoke-settle-content-verdict`+`settle-gate-and-probe-retry`+`verdict-honesty-repair`→page-verdict-gate（存活决定逐条注明来源收编：探针有限重试、同步原生调用隔离、绿跑留尾与目检纪律、`Ryn#101` 关闭认错）；直接删 1 篇（`settle-per-arch-window`，唯一论据已被两腿同值推翻）；保留 12 篇原样 + 6 篇保留截断。run id 降为 Testing 一行痕迹只施于 6 篇截断目标——**「保留原样」的 7 篇不动**（原样裁定优先于「全部 31 篇」的全称表述，本批的裁量点）。
- **新门禁（§7.1 末）**：`verify-adr-format.py --facts`——implemented 笔记正文中形如仓内路径/`DeepSeek.*`/仓内命名空间头的反引号 token，工作树零命中即 WARN（advisory，恒 exit 0）；globs/家目录/伪代码/上游树路径不探（精度优先）。消除「implemented 笔记引用已删符号」盲区的最低成本手段；存量 22 条 warning 为历史欠账，挂账待 Erratum 清理。
- **事实单一家（§7.2）**：架构/启动模型/运行时来源/插件装配 → `docs/architecture.md`（重写为回环代理源 + 三层端口 + 组合根新形态，B3 随之关闭）；`DSH_DESKTOP_DEV` 运行步骤 → `docs/development.md`；质量门清单 → `AGENTS.md`（规则）+ `docs/testing.md`（表格），`development.md` 只留一行链接；测试基线数字 → `test-baseline.json`；ADR 生命周期细则 → `.agents/notes/README.md`（AGENTS.md 收一行指针）。
- **死链与悬空指针（§7.3）**：修 `archived/` 内 10 条历史死链（纯链接修复，内容不动）；`verify-md-links.py` 排除面收窄——`archived/` 不再整目录跳过（排除面唯一家 = 脚本 docstring），`.noogenesis` 工具缓存入第三方跳过清单；durable 文档对未提交本地文件（HANDOFF 家庭、`.plan/`）的路径引用改语义描述（`AGENTS.md` 参考、`.agents/AGENTS.md`、`session-open/close.md`）。归档纪律随之和解：冻结例外 = 纯链接修复（见 notes README）。
- **预算（§7.4）**：`architecture.md` 950 / `testing.md` 850 / `development.md` 550 登记入 manifest；`notes/README.md` 800→400、`coding-standards.md` 1000→700 下调（实测 291/535，原上限虚高 2–3×）；AGENTS 预算表改为 manifest 的视图声明。
- **slop 与纠错（§7.5）**：durable 文档变更史措辞清洗（development/architecture-standards/cookbook 共 5 处）；`cookbook-retired-entry-cold-archive` 词数与指针位置自相矛盾按实测改 2469/页头；README 双语「三工程计数见 test-baseline.json」错误引用删除（基线文件只有 tests/coverage 两键）。

## Alternatives considered

- **把 6 篇截断目标的 run id 一并从 Problem 删除**：落败——Problem 的现象描述需要证据锚；降级为 Testing 一行痕迹已满足「可查不占骨架」，再删即丢可复核性。
- **--facts 模式做成硬门（零命中即 FAIL）**：落败——上游专属符号（如 Ryn 内部类型）与归档叙事会造成结构性误报，硬门会变成常红；advisory + 指向 Erratum 纪律是消除盲区的正确力度。
- **保留 `archived/` 整目录排除、只修死链**：落败——排除面正是死链永久不可见的机制（修完再漂移仍无门）；收窄排除 + 和解冻结例外（纯链接修复）才能持续覆盖。
- **architecture.md 保留旧文增量补丁**：落败——B3 定性为「描述代理层之前」的结构性过时，补丁无法收敛；整篇按当前状态重写，字数反而 886→809。
- **手改 SVG/HTML 使图同步新结构**：落败——产物可再生纪律（改 JSON → archify 重新生成）；本沙箱无 archify profile，手改产物引入不可再生漂移。图重生成挂账（见 Consequences）。

## Consequences

- 语料收益：活跃 ADR 178→162 篇，消失的 changelog 面不再参与评审与检索；三篇合并 ADR 的存活决定有唯一家。
- 门禁更诚实：归档面 686 个链接目标纳入校验；implemented 笔记的仓内引用有 liveness 探测（advisory）。
- 代价：归档纪律出现「纯链接修复」例外（规则文字已同步）；--facts 的 22 条历史 warning 在清理前持续出现在 advisory 输出中。
- **挂账**：① `--facts` 存量 29 条 warning 的 Erratum 清理；② 架构图重生成（`architecture.diagram.json` 需反映回环代理/端口形态，待 archify 可用环境执行「改 JSON → validate → deliver → 重抽 SVG → 双语 README 同步」流程）——图是视图非契约，漂移期以代码 + architecture-standards 为准；③ `bootstrap-window-ready-wait` 归档判据最弱（有界等待/异常收口仍属存活语义， rationale 无活跃承接）——后续触碰启动等待链时回 git 历史取该篇（R3 记账）。

## Testing

- `python3 scripts/verify-adr-format.py`（163 篇 OK；裁定净减 15 篇）+ `--self-test` 6 夹具全过 + `--facts` 输出 22 WARN 0 FAIL。
- `python3 scripts/verify-md-links.py`：686 目标 OK（含归档面 10 条死链修复后的重扫）。
- `python3 scripts/verify-doc-budgets.py --manifest …`：11 条全 OK（三新登记 + 两下调后实测均在限内）。
- `python3 scripts/verify-cookbook.py` / `verify-handoff-structure.py` / `verify-governance.py` / `verify-readme-badges.py` / `verify-compose-root.py` / `verify-code-health.py --enforce` / `verify-code-conventions.py --enforce` 全绿（docs 门禁面回归）。
- `dotnet test` 全绿（零代码变更；代码注释中 3 处 ADR 引用名改指现行家为唯一 src/tests 触碰点）。
- **CI 实跑（本批 done 判据，已通过）**：main push run `36414196841` —— docs / changes / build-test（ubuntu+macos+windows）五作业全 success。
