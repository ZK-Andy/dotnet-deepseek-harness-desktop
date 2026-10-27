# Agent Note: 冷启动起步前置与代理缓存头白名单

Status: implemented

Erratum: 2026-10-27 — 「502 根因仍未分型……届时需另立『启动期导航拦截』片」的立项条件不成立：铸币 303 即放行后多次冷启启动期 502 条数为零（用户实测确认，未记次数）；本决定与其代价不变。

Review: FULL/2026-10-27#3/R1=ok R2=ok R3=ok

## Problem

冷启动的体感延迟与启动期 502 来自三处独立成本，此前没有一份记录把它们放在同一时间轴上：

- **dsh 自身启动**：spawn 到输出 `dsh web:` URL 实测 **14.1–26.8s**（2026-10-26 安装版 0.7.5，Linux x64，单机 n=6 次冷启动，读 `~/.dsh/logs/host.log`；【探索性：非受控基准】）。
- **壳侧串行段**：窗口可见 **2–6s**（n=6）；铸币后的就绪稳定化 **5–7s**（n=5），而设计目标是 2s 稳定窗。
- **启动期 502 突发**：4/6 次冷启动各出现 **5 条** `代理未铸币即调用：502（等待15s…）`——WebView 在铸币前解析了上一次的 SPA HTML（【推断】）。

两处可归因的壳侧缺陷构成本次改动面：起点挂在 `window.Opened`，使窗口/WebView 初始化把 dsh spawn 串行化在身后；回包面只写 `Content-Type`，把 dsh 的缓存头全部丢弃，WebKit 持久缓存因此只剩启发式、冷启动重放旧文档。

## Decision

**起步前置（A2）**：`AvaloniaHost.Run` 在单实例仲裁、代理绑定与 Wiring 装配之后、`RunUi` 之前调用 `ShellStartup.Begin()`；窗口仍先亮，dsh spawn 与窗口/WebView 初始化从此并行。窗口显示后由 `ShellApplication` 的 `Opened` 接线补挂失败面与监督，接线以 once 守卫防 `Show()` 唤回重触发。

**晚挂载交付（B1）**：`ShellStartup` 的失败与「收口后接线」改为缓存式交付——先发生则缓存，挂载时补交付，各自只交付一次（`AttachFailureSink` / `AttachAfterStart`，内部 `_finished`/`_completed` 状态机）。建窗前 spawn 失败仍建窗、由失败面展示；失败或取消后永不接监督。

**代理缓存头白名单（C1）**：回包只透传 `Cache-Control`、`ETag`、`Last-Modified`、`Expires`（`ProxyHeaderPolicy.ResponseCacheHeaders`，响应头与 Content 头都查），holder 页显式 `Cache-Control: no-store`。白名单即安全边界：`Set-Cookie` 与 hop-by-hop 头永不进页面，壳不补默认值、不改写 dsh 原值。

**稳定化保持原样**：2s 稳定窗、双判据、30s fail-open 与串行探活全部不动。稳定化目标值从「≤4s」撤销——首探 `session/list` 冷启 2.4s 加 2s 稳定窗，算术地板即 4.4s，原目标不可达。

**启动期小项（P3）**：`StartupNoticeService` 在引导未介入时复用 `Resolve()` 探到的全局 dsh 版本，免去第二次 `dsh --version` spawn；自更新检查从建窗接线期推迟到首屏之后。

## Alternatives considered

- **A1｜仍在应用层触发，只把 `Begin` 挪到建窗前**：落败。收益与 A2 相同（`InitialUri` 是启动入参、不依赖 `Begin` 产出），但起点留在应用层会让「运行时由装配面起步」与单实例/代理/资源寿命三处事实分散在两个类型里；装配面已持有这三样，起步触发与它们同源。
- **B2｜建窗前失败直接退出码 1、不建窗**：落败。违反失败面验收（「窗口仍能显示失败面」），且与 WebView2 缺失走原生提示的既有口径不一致。
- **C2｜稳定化收窄（两面并行 + 第 2 轮只复探 mux）**：本批不取。省约 1.4s，但等于把「mux Ready ⇒ 会话面 Ready」写进行为契约——该蕴含目前只有注释级证据；把未证实的判据与低风险的头白名单捆在一批，失败回滚面变大。更细的节拍（1s→0.5s）收益约 0.5s、落在噪声里且多一个可调参数，同不取。
- **保留「稳定化 ≤4s」验收**：落败。首探 2.4s + 2s 窗地板 4.4s，判据不可达；继续挂着只会让验收永红。
- **把未铸币等待预算从 15s 抬到 60s**：落败。只把快速 502 换成长时间挂起，旧页面的错误请求仍在，且不解决缓存头丢失。
- **启动时清空 WebKit HTTP 缓存**：落败。243MB 持久缓存全清代价高、丢资产缓存，是钝器；白名单只恢复本该保留的缓存头。
- **删除 2s 稳定窗、单次 Ready 即放行**：落败。既有实机证据表明单次 Ready 会撞上「UI 先出、树晚 ~15s」的渐进渲染；可以收窄采样组织，判据不能退。
- **把所有 dsh 响应头原样透传**：落败。会带回 `Set-Cookie` 与 hop-by-hop 头，破坏「壳代持 cookie、页源零透传」的安全不变量。
- **进程一启动就 spawn、不等单实例仲裁**：落败。第二实例会先拉起 dsh 再退出，制造孤儿与端口竞争；spawn 必须在单实例与代理之后。
- **先删 profile 的 12 个 bundle 以换启动速度**：落败。功能取舍属产品决策，删插件会改变用户能力面；2026-10-27 否决——连评估都不立项。

## Consequences

- 窗口/WebView 初始化（日志间隔推断 1–3s）与 dsh spawn 现在完全重叠；首启探测后少一次 `dsh --version` spawn；自更新请求不再与首屏抢带宽。
- **502 根因仍未分型**：若重放触发点是 WebKit 会话恢复而非 HTTP 缓存，头白名单只恢复静态资产缓存，502 不减——届时需另立「启动期导航拦截」片，本批不承诺 502 归零。
- **dsh 若不发 `Cache-Control`，壳不补**：旧文档重放可能仍在 holder 之外发生；是否给 HTML 补默认 `no-store` 是产品决定，未纳入本批。
- **冷启动总量收益只剩并行段**：稳定化 5–7s 未动，原「总量降 ≥5s」不再作为验收，改分段记账（spawn 早于窗口可见 / 稳定化耗时不变 / 502 条数）。
- **失败可见性延迟**：失败面在 `Opened` 后弹出，失败发生到用户可见之间有窗口初始化延迟；建窗前失败不会静默消失，但也不再早于窗口。
- **晚挂载状态机**：`ShellStartup` 新增两个一次性交付面，挂载点唯一（`Opened`）；重复 `AttachAfterStart` 会覆盖前一个动作，当前只有单一调用方。

## Testing

- `DeepSeek.Harness.Desktop.Infrastructure.Tests`（616 例）：白名单四项透传/白名单外丢弃/`Set-Cookie` 不进页面（经真回环 socket）、holder `no-store`、版本复用免第二次探针。
- `DeepSeek.Harness.Desktop.Tests`（270 例）：`ShellStartup` 晚挂载四态（失败先于挂载、收口后挂载立即跑且只跑一次、失败/取消不接监督）。
- 本地合计 1123 例全绿；CI 实测 1123/67.55% 已回填（数字以 `scripts/test-baseline.json` 为准）。

## Related

- [avalonia-g0-entry-switch](../architecture/2026-10-22-avalonia-g0-entry-switch.md)：`Opened` 后起步的次序由本决定取代，窗口先亮不变。
- [avalonia-g1g3-recovery-bootstrap-supervision](../architecture/2026-10-23-avalonia-g1g3-recovery-bootstrap-supervision.md)：同一触发点的监督接线，已随本决定改为 `Opened` 补挂。
- [loopback-forward-proxy](../architecture/2026-09-27-loopback-forward-proxy.md)：其「响应头最小集」TODO 由本决定收口。
- [mint-epoch-mux-gate](2026-10-02-mint-epoch-mux-gate.md) 与 [relay-restart-client-module-collapse](2026-09-19-relay-restart-client-module-collapse.md)：稳定化判据来源，本批不改其判据。
- [runtime-starter-core-usecase](../architecture/2026-10-08-runtime-starter-core-usecase.md)：起步用例的端口与测试面，沿用未重写。
