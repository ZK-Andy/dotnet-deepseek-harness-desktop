# Agent Note: 落定窗与 arm64 冻结随旧链路退役

Status: implemented

Erratum: 2026-09-28 — 残留边界记账的落点换家：三处宿主导航点随组合根形态分离迁出（ADR architecture/2026-09-28-compose-root-form-separation）——收养恢复与健康 reload 现在 `Bootstrap/StartupSequence.Supervision.cs`（NavigateAfterAdoptAsync/SetupHealthMonitor），鉴权重载在 `Bootstrap/StartupSequence.WebSession.cs`（SettleWebSessionAsync）；记账对象不变。

Review: FULL/2026-09-28/R1=ok R2=ok R3=ok

三审结论：R1 1 Blocker + 2 Suggestion，**全采纳**——删掉被删变量留下的孤立注释（`smoke-install-linux.sh` 中「动态作用域」说明的指称对象 `local frozen=""` 已随本批消失，共享库实际以全局读 `SETTLE_WAIT`）；workflow 软件渲染注释的「第二跳」措辞随旧链路更新；补记 120s 用户令上限的处置并修正 Alternatives 里「上限与矩阵值同源于 285s 推导」的错误归因（二者不同源，上限的实施面消费者才是矩阵值）。R2 0 Blocker / 0 Suggestion——并排除一处实现报告的文字笔误隐患：见证门引用的是 `SMOKE_SHOT_DIR`（`SHOT_DIR` 全仓零定义），像素见证门未被静默关闭；rc 链闭合无静默降级，三平台默认值一致。R3 0 Blocker + 5 Suggestion，**全采纳**——两篇被 supersede 笔记按勘误纪律各补 `Erratum:` 行（正文不动，避免 `implemented/` 语料继续断言已删键）；撤掉无据的「1/6 资产」分数，改为可辩护的「linux-arm64 deb 这一个包」（豁免调用点仅在 `smoke_deb`，arm64 的 rpm 容器腿无 X、恒 install-chain 够不到）；补 run/job ID 使数值可复核；删掉对未提交草稿的引用。R3 独立用 `gh` 复核了本笔记全部数值断言，并补充一条更准确的事实——转向后前两次 arm64 run 的像素见证本身未过、由客户端存活 OR 门判过，仅 tag 那次真过见证（与 amd64 同轮待遇一致，已据实改写）。无拒绝项。

## Problem

CI 的 Linux 冒烟门上有两处「为让红变绿」而生的机制，其依据都随页面投递链路的转向而消失。

**冻结（arm64 豁免放行）**：`package-linux.yml` 的 arm64 腿带 `frozen: 1`，落定失败但已见信号①时打印 `SMOKE_VERDICT=frozen-native-hang` 并把 `rc` 改 0 —— **不拦发版**。三条事实说明其前提已失效：

- **决策早于转向 5 小时**：冻结在 `b0eba4b`（2026-09-27 00:19:16），对标官方的转向在 `2ec50cf`（05:25:30）与 `334c8cd`（12:20:51）。冻结那一刻不可能有转向信息。
- **病灶所在的机制已被整体删除**：当时的失败签名是宿主自身两跳导航在 saucer 同步段挂住（`[nav] 导航调用超时（30s）`×2 + `[nav] 页面裁决=unknown` + `eval timed out`×4，全日志唯一到达 `ryn://app/index.html`）。转向后窗口 URL 恒为回环代理源、**启动链零 host 导航**（ADR loopback-forward-proxy），承载该手势的 `DshSchemeBridge`/scheme 定制面/eval 首跳绕行已全部 0 命中。
- **冻结的退出条件已死**：原注释写「解冻待 Ryn 最小复现有回音」，而 `Ryn#101` 已被本仓关闭认错（ADR verdict-honesty-repair）。等一个自己撤回的 issue 等于永不解冻。

实证：转向后 arm64 三次 run（`36318447847` / `36321158329` / `36325106381`）全部落定 2–3s、verdict=`full-chain`，`frozen-native-hang` 一次未现。其中 tag v0.5.8 那次（run `36325106381`，arm64 job `108636263981`）豁免**已装填（`SMOKE_FROZEN: 1`）却未被使用**——落定返回 0 故 `frozen` 标志从未置位，脚本继续跑到像素见证门并通过（mean=0.798776，同轮 amd64 job `108636263878` 为 0.80966，两架构不可分），即 arm64 是在与 amd64 同一道门下真过的。另两次的见证本身未过（mean≈0.988），由「客户端存活」OR 门判过——与 amd64 同轮的待遇一致，不构成 arm64 独有缺口。

**落定窗（死配置 + 三平台不一致）**：CI 经矩阵传 `settle_seconds: 300`，并附一段「建窗残量 120 + 两跳(30+5)×2 + 探针 15×2 + 重进(35) + 再探针 30 ≈ 285s」的推导注释；而脚本有 `SMOKE_MAX_SETTLE_SECONDS=120` 硬上限把 300 夹回 120 —— 每次 CI 日志都打 `note: 落定窗 300s 超过上限 120s，按上限执行`。**那段 285s 推导描述的是两跳导航链，该链已不存在**，故矩阵值与其推导注释同为假陈述。同时三平台不一致：linux 被夹到 120，mac/windows 无上限分支、实为默认 90。

## Decision

两处机制作为同一个逻辑单元退役——它们的依据同源于已删除的旧链路。

- **workflow**：删矩阵两腿的 `settle_seconds` 与 `frozen` 键、删 285s 推导注释与 arm64 冻结说明、删 smoke 步 `env` 的 `SMOKE_SETTLE_SECONDS` 与 `SMOKE_FROZEN` 传递。矩阵只留 `arch`/`rid`/`runs-on`。
- **脚本**：删 `SMOKE_MAX_SETTLE_SECONDS` 夹紧块与其 `note:` 回声，`SETTLE_WAIT` 回归单口 `SMOKE_SETTLE_SECONDS:-90`（**三平台自此同值**）；删 `smoke_frozen_pass()`、其在落定失败路径的调用点、`frozen` 局部变量及其在见证门与 verdict 打印处的全部管道、三条冻结自测夹具。
- **落定失败不留任何放行路径**：`rc -ne 0` 就是失败（原冻结分支的 `rc=0; frozen=1` 删除后不加替代兜底）。
- **120s 硬上限（用户令）的处置**：该上限要求「落定窗不得超过 120s」，其实施面唯一消费者是已删的矩阵值（300 被夹到 120）；配合固定 90s 单值后它已无夹紧对象，故随矩阵值一并退役，且由 **90s** 更强地满足（90 < 120）——「不许加时间换绿」的原则以「三平台单值、任一腿不得独立加窗」的形态保留。另：原上限的表述在 `smoke-witness-real-and-eval-first-hop` 正文亦被提及，那属该篇的现状陈述，由本批的勘误行处置。
- **残留边界随本笔记记账**（不为它们加门）：三处宿主导航点走同一原生 `set_url` 且 CI 不覆盖——`DesktopBootstrap.App.cs` 的收养恢复与健康 reload、`DesktopBootstrap.WebAuth.cs` 的鉴权重载；arm64 建窗比 amd64 慢约 10s，属未解释余量，当前 90s 窗内富余充裕。
- **首轮转红按签名分型**：出现 `导航调用超时` 家族才回退冻结，且应附新证据**重开新的上游 issue，不复用已关闭认错的 `Ryn#101`**；见证红且客户端存活 OR 门也不满足 → 属新病灶，单独立项。

## Alternatives considered

- **保留冻结、只补一个到期条件**：落败——冻结的根因当年从未被正面确定（结论是「managed 边界之下三选一未定」），写不出可判定的到期条件；而「门看着在、实则常开」比明说没门更坏。
- **把 arm64 降级为「只取证」不判门**：落败——实测 arm64 已能过与 amd64 同一道门（含像素见证），放弃一个可用门没有收益；证据落地后该选项被推翻。
- **保留 120s 上限、只把 CI 值下调到 ≤120**：落败——上限的实施面唯一消费者就是已删的矩阵值，值删后上限没有夹紧对象，留它即留一处无消费者的配置；且其 285s 依据描述的链已不存在。
- **保留 `frozen` 变量但恒空**：落败——死代码，且下一位读代码者无法判断它是否还有语义（与「用完即删」的诊断残留同类）。

## Consequences

- **arm64 从豁免放行回到真门**：落定失败即红、即拦发版——这是本批的目的。豁免原本只可达于一条腿：`smoke_frozen_pass` 的调用点仅在 `smoke_deb`，而 arm64 的 rpm 腿跑在无 X 的容器里、恒判 install-chain，够不到该分支，故实际覆盖面是 **linux-arm64 deb 这一个包**。
- **三平台落定窗统一为 90s 单一口**，此前 linux=120（夹紧后）/mac=90/win=90 的不一致消失；实测落定用时 2–3s 是该窗所覆盖的段（① 之后至落定），余量约 30×（建窗段由 `SMOKE_WAIT` 另行覆盖）。
- 冒烟自测从 40 条降至 37 条（三条冻结夹具退役），其余断言全数保留、全绿。
- **残余风险**：若 arm64 真链需 >90s，本批后会由绿转红——按上「分型处置」应对，不预先放宽窗口（「加时间换绿」正是本批要清掉的形态）。

## Related

- `2026-09-26-settle-per-arch-window`（已删，E 批 ADR 裁定）：本批 supersede——其「amd64 180 / arm64 300 分腿」论据早已被两腿同值推翻，矩阵列随本批删除；唯一论据无安全/契约不变量，不保留。
- [2026-09-26-smoke-witness-real-and-eval-first-hop](../../archived/testing/2026-09-26-smoke-witness-real-and-eval-first-hop.md)（已归档）：本批 supersede 其冻结面（`frozen` 矩阵 + `SMOKE_FROZEN` + `frozen-native-hang` 放行 + 正文提及的 120s 上限）；该篇的 eval 首跳主体已由转向退役。
- [2026-09-27-loopback-forward-proxy](../architecture/2026-09-27-loopback-forward-proxy.md)：转向 owning 笔记，「启动链零 host 导航」的出处，本批的直接前提。
- [2026-09-26-page-verdict-gate](2026-09-26-page-verdict-gate.md)：落定门 owning 笔记（吸收合并 `verdict-honesty-repair`，`Ryn#101` 关闭认错的现行出处）。

## Testing

- `bash scripts/smoke-install-linux.sh --self-test` → **37 ok / 0 fail，exit 0**（三条冻结夹具退役后其余断言全保留）。
- `python3 scripts/verify-governance.py` → 0（workflows 改动后 `run:` 禁插值规则仍绿）。
- `bash -n scripts/smoke-install-linux.sh` → 0。
- 全仓可执行面 grep `settle_seconds` / `SMOKE_MAX_SETTLE_SECONDS` / `SMOKE_FROZEN` / `smoke_frozen_pass` 零命中（仅本笔记与其 supersede 对象的历史正文提及）。
- **实跑验证（本批 done 判据，已通过）**：`package-linux.yml` dispatch run `36332961407`（commit `095b459`）——**两腿全绿、无豁免**。arm64 腿：`行为落定（①+铸币303+客户端存活，用时 3s）` → `截图内容见证通过（mean=0.805255 sd=0.138811）` → `SMOKE_VERDICT=full-chain`；amd64 腿同形（2s、mean=0.79997）。日志中 `SMOKE_FROZEN` 一行皆无、无 `frozen-native-hang`、无 `导航调用超时`，窗口打印为 `落定90s` —— 冻结与夹紧两处机制均已不在链上。按 feature-flow 步骤 3，`.github/workflows/**` 变更的 dispatch 实跑由此满足。
