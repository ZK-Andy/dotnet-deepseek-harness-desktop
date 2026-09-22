# Agent Note: relay-restart-client-module-collapse（市场自重启后活页面整树热替换塌陷，侧栏会话与归档列表全空）

Status: implemented

Review: FULL/2026-09-19/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

插件更新触发 dshmarket 自重启后，桌面端**仍开着的页面**侧栏会话列表与归档列表**同时全空**（界面仍在、主区无会话）；Ctrl+R/F5 无效果；整机重启后恢复。每次插件更新必现（2026-09-19 02:31 实机：`~/.dsh/logs/host.log` 记「子进程退出 → 市场接力续任者接管端口 → 收养续任者 pid 5709 → 导航到裸 origin」，此后无任何页面健康迁移）。

**机制（实证）**：

- `dsh-client-modules` 每个进程 boot 生成随机 `initialRevisionNonce`（上游 `packages/client/modules/src/index.ts:566`；本机 `dsh-client-modules/lib/index.js:489`），全部 client 模块 URL 带 `&rev=<nonce>-<n>`。
- 宿主在**每条新 SSE 连接**上推完整 graph（`dsh-client-hmr/lib/index.js`：端点 `/plugins/events`，`connect()` 首帧 `type: "graph"`），客户端收到即整树 replace（`dsh-client-hmr/lib/client.js:56` → `entries.sync`）。
- 故**任何**存活页面在服务端重启后重连，都会把整棵 client 模块树换成新 boot 的 rev；替换后客户端 Cordis 树的服务与适配器失联。

**隔离复现（2026-09-19，n=1 环境 × 4 次触发；【探索性】）**：独立 `DSH_HOME` + 探针 profile（复制 `package.json`/`cordis.yml`/`cordis.patch.yml`/`node_modules`/`storages` 与近 12 个 session）+ headless Chrome（CDP 驱动，页面内只读采集），触发路径＝`POST /dsh-market/restart`（与市场 UI 一键重启同一 handler）。

- 基线：侧栏 8 个工作区与全部会话正常渲染。
- 重启后（不重载页面）：60 个模块 URL 全部换成新 nonce（实测 `bb8f8a1b976995d9-0…59`，页面共存 62 个 rev 值），控制台 `cannot get required service "remote" in inactive context`、`conversation.input: sessions service unavailable`、`scope 'session-maybe' rendered without an installed adapter`；`document.body.innerText` 归空，3 分钟后仍不自愈。
- 对照：**不改任何插件**的纯重启同样塌陷（复现 2 次）——与“插件是否变更”无关。
- 恢复：同一 URL 重新加载一次即回到健康（无错误、侧栏完整）。

**壳侧放大与观测盲区**：

- 接力就绪判据是「HTTP 有非空应答或 3xx」（[RuntimeLineageProbes.cs](../../../../src/DeepSeek.Harness.Desktop.Infrastructure/Runtime/RuntimeLineageProbes.cs)）；实测市场自重启窗口内**将死的前驱进程仍应答 `401` + 68 字节正文**，按该判据即 Ready，壳随即收养并导航，页面落进「连上随即断线」的窗口。实机那次 Ready 由将死前驱还是未挂全的续任者应答未取证【推断 · 未证】——两者同属「判据过早」类。
- 页面健康探针只查 `body.childElementCount`（[PageHealthMonitor.cs](../../../../src/DeepSeek.Harness.Desktop/PageBridge/PageHealthMonitor.cs)）；塌陷页面仍有 3 个子节点 → 判 alive，既有 3×10s 阈值与有界 reload 永不触发，页面对用户永久不可用。

## Decision

两条判据同批收紧，互为兜底：

**A｜页面健康判据改为「有可见内容」**：`PageHealthMonitor.ProbeScript` 回报 body 的可见文本长度观测值（`text:<len>`，len = `innerText.trim().length`，body 缺失按 0），由 `PageHealthMonitor.Parse` 作「DOM 形态 → 健康态」的单一判读点：`text:0` 判 Dead、`text:<n>`（n>0）判 Alive、空串/形状不认识/非十进制数字判 Unknown（不计入探针计数，绝不因读不懂观测值触发恢复）。既有 3×10s 阈值、有界恢复预算与 Alive 复位语义不变：塌陷页在 ≤30s 内被判 Dead 并触发一次有界 reload 复原，预算耗尽仍转观测-only。壳自有文档（`wwwroot` 引导页、崩溃恢复页）留有脚本/样式之外的可见文案，在新判据下仍判 Alive，由夹具钉死。

把判读收进 C# 纯函数（而非让脚本直接回 alive/dead）是为了让「DOM 形态 → 健康态」表判可单测；脚本与判读点的契约（单一 `text:` 观测值、`innerText` 而非子节点数）另由契约用例钉死。

**B｜接力就绪加稳定窗**：接力共存窗口（`TryRideMarketRelayAsync`）的就绪判据在 `Ready` 之外再要求**连续维持**：`RelayWebReadinessGate` 从首个 `Ready` 起计时，期间出现任何 `NotServing`/`ServingNotReady` 采样即清零重计，连续维持满 `s_relayReadyStableWindow`（2s，与 1s 探测节拍同源 ≈ 连续 3 拍）才进收养链。接管命中行追加稳定窗措辞（`市场接力续任者已接管首选端口 <port>（就绪连续维持 ≥2s）`），既有判读前缀不变。该窗只作用于接力共存窗口；spawn 失败后的收养路径（`RecoverFromFailureAsync`）不过稳定窗——它已付过一次失败 spawn，且端口占用者已由 bind 冲突证实在服务。

B 选「连续 Ready 满窗且无断连」而非「端口先出现一次拒绝后再 Ready」：helper 快速重绑时端口释放窗口可能观测不到，要求「先见拒绝」会把收养拖到预算耗尽、回落 spawn 路径（那条路径不过稳定窗），把「晚 ~2s 收养」换成「可能完全绕过判据」。

C（不再收养续任者）的复访触发见 `## Deferred`；D（上游把重连热替换做成原子重建或整页重载）留上游。

## Alternatives considered

- **只收紧接力就绪判据（不做 A）**：落败——塌陷由重连触发，而重连在崩溃恢复、网络抖动等路径同样发生；判据再严也挡不住后续任意一次重启。
- **只收回重启权（C，不做 A）**：落败——上游缺陷仍在，壳自身 spawn 后 dsh 崩溃重启时活页面照样塌，且付 ~40s 窗口；未验证 A 收益前不取。
- **页面内自救（前端/companion 触发 reload）**：落败——塌陷态下 `remote` 服务已不可用，页面内通道不可靠，且侵入 dsh 渲染面。
- **等上游修复**：落败——上游仓库 issue 已关闭，无可跟踪通道；症状用户可见且阻塞，壳侧兜底自足。
- **探针改为 HTTP 探测或 companion 握手**：备选未取——HTTP 探针看的是服务端而非页面；companion 握手需新 IPC 帧与两侧改动。A 的纯 DOM 判据成本最低；实机误判率不达标再回到此案。
- **接力就绪要求「端口先出现一次拒绝后再 Ready」**：落败——见 Decision 的 B 条：helper 快速重绑时释放窗观测不到，收养会被拖到预算耗尽并绕过判据，比固定 ~2s 的稳定窗更不可控。
- **把稳定窗也加到 spawn 失败后的收养路径**：未取——该路径的端口占用者已由 bind 冲突证明在服务，再加等待只推迟恢复；稳定窗的靶心是「将死前驱仍应答」这一共存窗口形态。

## Consequences

- 塌陷页 ≤30s 内自动 reload 复原（有界预算内），用户不再需要整机重启；无插件变更的纯重启（崩溃恢复）同样被覆盖。
- 接力收养比 v0.5.3 晚 ~2s（稳定窗）：恢复屏多显示约 2s，换掉「导航进将死前驱窗口」。
- 观测面：探针快照仍为 `alive/dead @ <时间> (probes N)`（`Parse` 语义不变，诊断包口径不动）；接力接管行加稳定窗措辞。
- 内容判据依赖 `innerText`（布局相关）：窗口隐藏或极短合法页面可能短暂判 Dead——3×10s 阈值提供缓冲，恢复动作是有界 reload，最坏多刷一次页面。
- 接力窗口里就绪判定先于预算判定（既有次序，本批不动）：命中点最多比 `timeout` 晚一个探测迭代（≤2.5s）；恢复时限 60s 下无实害，且边界处收养一个已接稳的续任者优于回落一次注定失败的 spawn。
- 上游若把 rev 稳定化或修好热替换，A/B 与上游实现解耦、无害保留。
- 【探索性】结论基于 n=1 隔离环境（3 次重启 + 1 次无变更对照）；真机 Ryn/WebKitGTK 上的 DOM 形态（“界面在、列表空”对“整页空白”）与内容判据误判率仍待实机复验。

## Deferred

- **C｜不再收养续任者，改由壳自 spawn**：检出市场 helper 即收割，由壳自 spawn，等自家 dsh 给出 URL 后按 token URL 导航（冷启动路径，新 URL 天然绕缓存）；代价是 ~40s 恢复窗。**复访触发 = A/B 的实机验收结论**——塌陷仍落在用户可见窗口内，或稳定窗把收养推迟到不可接受，则评估 C。
  复访触发已满足一次（2026-09-20/21 实机验收）：两次市场接力均无可见塌陷、稳定窗延迟未被报告为不可接受 → **C 维持 Deferred**；触发条件保留，下次实测出现可见塌陷或收养延迟不可接受时再评估。

## Testing

- 探针契约（`PageHealthMonitorTests`）：DOM 形态观测值 → Dead/Alive/Unknown 表（含带符号、前导空白、非十进制、旧观测值等乱值）；脚本 token 契约（只回报 `text:`、用 `innerText`、无 `childElementCount`）；壳自有文档夹具（`wwwroot/index.html` 真实产物的可见 `<h1>` + 恢复页骨架里作为元素文本落地的文案）在内容判据下判 Alive。
- 稳定窗（`RelayWebReadinessGateTests`）：单拍 Ready 不接稳；连续满窗接稳；`NotServing` 与 `ServingNotReady` 均清零重计；非正稳定窗 fail loud（不退化成单拍即接稳）。
- 接力窗口（`HarnessRuntimeHostTests`，Linux 真 `/proc` 探针 + 假 helper/续任者 + `LoopbackHttpResponder`）：走真 1s 节拍、两拍 Ready 后断连 → 窗口内不收养、无接管留痕（把稳定窗降到一拍即会红）；既有「稳定 Ready → 一次收养」用例继续覆盖正路。
- 实机验收（2026-09-20 05:31 / 2026-09-21 06:02，Linux 真机，随 v0.5.4/v0.5.5 装机触发，取证 `~/.dsh/logs/host.log`）：市场接力两次（续任者 pid 5717 / pid 10860），就绪连续维持 ≥2s 后收养并导航，页面持续可用（收养后仍能拦截外链导航）；修复上线后日志 `dead` 与有界恢复各 0 次，侧栏会话与归档列表未见空白、无需整机重启。安装器首启补装一项无独立留痕（各次冷启均走「随包插件无需安装（companion 已就位），跳过」），不据此销账。

## Related

- [relay-web-readiness](2026-09-16-relay-web-readiness.md)：本决定在其「web 面可服务」之上加稳定窗，并记录该判据仍可被将死进程/未挂全面满足。
- [market-restart-adopt-first](../feature/2026-09-15-market-restart-adopt-first.md)：收养续任者的决策；C 备选即其取反。
- [runtime-handoff-adoption](2026-09-12-runtime-handoff-adoption.md)：收养语义与 cookie/authority 前提；本次复验鉴权非本缺陷成因。
- [page-health-monitor](../process/2026-08-26-page-health-monitor.md)：A 所改探针与有界恢复的提供者（阈值、预算、Alive 复位语义均以彼为准）。
- 复现现场（gitignored）：`.plan/probe/logs/*.png`、`.plan/probe/tools/cdp.js`、`.plan/probe/tmp/dsh-market-restart-*.out|err.log`。
