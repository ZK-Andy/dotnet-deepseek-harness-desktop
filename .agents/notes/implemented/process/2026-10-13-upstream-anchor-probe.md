# Agent Note: 上游锚点探测（注入面漂移闸）

Status: implemented

Erratum: 2026-10-15 — 决定撤销（跟随上游接受漂移，见新篇 [2026-10-15-upstream-anchor-gate-removal](2026-10-15-upstream-anchor-gate-removal.md)）：周检工作流、探测脚本与锚清单已删；正文保留为历史，不再指导现实。

Review: FULL/2026-10-14/R1=ok R2=ok R3=ok

Related: 注入机制来源 [`frameless-uniform-caption-bar`](../feature/2026-10-07-frameless-uniform-caption-bar.md)；通道先例 [`2026-09-29-dsh-follow-next-channel`](../architecture/2026-09-29-dsh-follow-next-channel.md)；同族先例 [`2026-10-12-coupling-increment-baseline`](2026-10-12-coupling-increment-baseline.md)（脚本 + 工作流 + 门禁位）；锚登记册 `scripts/upstream-anchors.json`（已删，见 Erratum）。

## Problem

壳不"拥有"页面：chrome 与横幅靠注入 CSS 挂钩上游的 DOM 类名后缀（`[class*="sidebarCol"]`、`[class*="logoRow"]`）与 `--dsw-*` design token。这两面在上游动得很勤——2026-06 起非 merge 提交：`ui-layout/src/client` 65 次、`ui-theme` 的 `design-platform.css` 24 次（9 月各 27 / 19 次）。

失败模式是静默的：上游把类名或变量改名，注入照旧执行、页面不报错，只是少一条视觉规则（侧栏让位、描边色标、横幅配色之一），只能等用户机上发现。实测例：上游 2026-09-30 才新增 `data-shell-bottom` 槽位，本壳 10-07 把它写进侧栏让位的**并列选择器组**（与 `[class*="sidebarCol"]` 同列，两条无主次）——而它不在正式版通道（`@next` = 0.2.0-rc.2）里，正式版上该条不命中，让位由同列的类名分支承担。

同批暴露的第二个问题：通道语义在注释与文档里被写成「`@next` 预发布通道」，与产品事实相反（`@next` = 正式版通道，`@alpha` = 预览版）。

## Decision

- **通道语义锁定**：`@next` = 正式版通道（产品默认 `RuntimeBootstrapOptions.DshSpec`、首启引导的 `npm install -g`、本闸断言基准）；`@alpha` = 预览版，不作产品默认。运行时分两侧说：**探测侧只读**——`RuntimeVersionGate.ProbeAsync` 只读 PATH 上用户自装的全局 dsh，不改动它；**引导侧会写**——首启在「未检出或低于兼容底线」时按 `DshSpec` 执行 `npm install -g`（装到正式版通道，或把低于底线的更新过去）。[docs/architecture.md](../../../../docs/architecture.md)「运行时来源」与相关 XML doc 按此表述。
- **在册锚的唯一家** = `scripts/upstream-anchors.json`（已删，见 Erratum）：每条 `id`/`token`/`tier`/`used_by`/`impact` 五字段必填（脚本校验）。入册两条判据：①有真实消费点；②**可检测**——改名后旧串不会因无关代码残留（`collapsed`/`toggle` 这类通用单词因此不入册：它们在 bundle 里恒有独立出现，锚会恒绿、零检测力；这两条规则里的可辨识锚 `sidebarCol`/`logoRow` 已在册。本壳自发布的 `--dsh-frame-*` 是发布量而非上游锚，同样不入册）。**命中判据按 token 形态定**：`--` 开头按定义形（`token[^A-Za-z0-9-]*:`——改名后残留的 `var(--old)` 引用不得算命中），其余按右侧标识符词界（`(?![A-Za-z0-9_$])`——`sidebarCollapsed`、`headerActionsHidden`、`sidebarCol_extra` 不得顶替同名锚）。tier 两类：
  - `required`：本次探测的安装必须有，缺即违规（`--enforce` 判红）。
  - `preview-only`：预览线先行、正式线尚未含的锚（如 `data-shell-bottom`）；缺只打印 `note:`，不算违规——正式通道才是产品承诺面。
- **探测源 = 已装的 dsh 包**（`dsh-client-*/lib`、`dsh-web-frontend/dist` 这类客户端资产）：正是 `dsh web` 端出来、本壳注入的那些字节，故离线、也不必猜上游 tag。**包根解析**：显式 `--dsh-root` 即权威——不是 `@deepseek-ai/dsh` 包根就 `FAIL`（exit 1），绝不回退去探别的安装；未显式给时才自动解析（`npm root -g` → PATH 上 `dsh` 的 realpath），两路皆无即 `SKIP`（exit 0——没装全局 dsh 的贡献者不算门禁命中）。**无法判定 ≠ 通过**：解析到包根但其下没有客户端资产（布局变更或资产被裁）时恒 `FAIL`（exit 1，与 `--enforce` 无关），不把「探不到」误报成 required 全缺、也不放行。
- **执行节奏**：独立工作流 `.github/workflows/upstream-anchors.yml`（已删，见 Erratum）（每周 `schedule` + `workflow_dispatch` + 改锚清单/探测脚本/该工作流时 `push`）装 `@next` 后跑 `--enforce`；本地随时可跑。
- **判据边界**：只断言 token 存在，不重验选择器语义（作用域、优先级、层叠）。token 还在而规则不再命中属视觉面，留给真机/视觉回归。

## Alternatives considered

- **对上游 tag 抓源文件（`AppFrame.tsx`、`design-platform.css`）**：落败——要网络与额外抓取，且探的是仓库源而非用户机上那份构建产物（tag 与本地安装可能不同版本）；离线探已装资产与注入对象同源。tag 形态留作发版前预判，本批不做。
- **把通用单词类名（`collapsed`/`toggle`）一并入册**：落败——它们在 bundle 里恒有独立出现（改名后 `sidebarCollapsed` 这类近名仍在），锚会恒绿、零检测力，只把「已覆盖」的错觉写进登记册；这两条规则的可辨识锚（`sidebarCol`/`logoRow`）已在册。判据②只作筛选、不替代①：它挡得住恒绿词，挡不住独立出现面广的 token（NODRAG 簇的 `headerActions` 也出现在别的客户端 bundle 里）；两条判据都要过，入册与否最终由①（真实消费点）定。
- **以 master / 预览线形态为主判据（把 `data-shell-bottom` 记为 required）**：落败——它正是本轮实测到的反例：预览线有、正式线没有；主判据必须取正式线成立的锚。
- **只改文档、不做门禁**：落败——锚漂移的失败模式静默（无异常、无日志），文档拦不住。
- **塞进 `ci.yml` 每次 push 跑**：落败——需要一次全局 npm 安装，而锚只在 dsh 发版时变；改为周检 + 自身路径改动时自验。
- **选择器级断言（起真实 dsh 页面、断言让位规则命中）**：落败（本批）——要起 `dsh web` 且带会话态与 WebKit/Chromium，成本与依赖远超一条 token 断言；留作视觉回归备选。
- **改用「拷贝 UI + 自有协议」形态摆脱上游漂移**：落败——上游 `packages/client` 每月 1600–2200 次非 merge 提交，拷贝等于放弃跟进或自建同步工程。

## Consequences

- 收益：required 锚被上游改名会在周检里判红，而不是等用户机上发现。
- 代价与已知边界：只在装了 dsh 的环境生效（无安装即 SKIP）；周检有最长 7 天发现延迟；token 存在 ≠ 选择器仍命中（本批不盖）。
- 清单维护：新增锚必须同批给出 `used_by` 与 `impact`（字段缺失由脚本判红）。

## Testing

- `python3 scripts/verify-upstream-anchors.py --self-test`：离线夹具覆盖包根解析、资产筛选（无资产目录）、命中定位、required 缺失判红、preview 缺失只 note、右侧词界（`TOKEN_A_LONGER` 不得顶替）、CSS 变量定义形（`var(--x)` 引用不算命中、`-hover` 近名不遮蔽）、清单形状（未知 tier / 重复 token / 缺字段 / 全 preview-only）、缺清单。
- 本机预览线实跑（dsh 0.2.1-alpha.2）：13 条 required 全在；`data-shell-bottom` 命中 preview 分支。
- 本机正式线实跑（临时 prefix 装的 `@next` = 0.2.0-rc.2，经 `--dsh-root .cache/dsh-next/lib/node_modules/@deepseek-ai/dsh`）：13 条 required 全在、`data-shell-bottom` 不在（preview-only，符合预期）。
- CI 取证通道 = `.github/workflows/upstream-anchors.yml`（已删，见 Erratum） 的 push / schedule / dispatch 三触发，本批 push 触发首跑；**首跑之前不计为已取证**，run 记录随交接条目留痕。
