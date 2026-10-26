# Agent Note: 冷启动等待链收敛与代理缓存头透传（502 突发修复批）

Status: proposed

## Problem

冷启动的体感延迟与启动期 502 来自三处独立成本，当前没有一份记录把它们放在同一时间轴上：

- **dsh 自身启动**：spawn 到输出 `dsh web:` URL 实测 **14.1–26.8s**（5 次成功就绪样本；6 次冷启动中 09:07 在就绪前被隐藏）。
- **壳侧串行段**：窗口可见 **2–6s**（n=6）；铸币后的就绪稳定化 **5–7s**（n=5），而设计目标是 2s 稳定窗。
- **启动期 502 突发**：4/6 次冷启动各出现 **5 条** `代理未铸币即调用：502（等待15s…）`，等待不是网络故障，而是 WebView 在铸币前解析了上一次的 SPA HTML（推断）。

### 取证范围

2026-10-26，安装版 0.7.5，Linux x64，单机 n=6 次冷启动，读 `~/.dsh/logs/host.log`。这是【探索性：n=6、单机单日、非受控基准】，用于定位等待点，不作为性能承诺。

20:54 这次的代表时间线：

```text
20:54:38  进程起
20:54:43  窗口可见 + dsh spawn（+5s）
20:55:03  dsh web 就绪：19.1s（+25s）
20:55:10  稳定化完成：7s（+32s）
20:55:16  页面首条 session/list 返回（+38s）
```

| 段 | 实测 | 归属 |
|---|---:|---|
| 进程起 → 窗口可见 | 2–6s | 壳（Avalonia/WebView 初始化、同步版本探测） |
| dsh spawn → URL | 14.1–26.8s | dsh 与 desktop profile 插件树 |
| 铸币 → 稳定化放行 | 5–7s | 壳（`DshShellForward` 就绪门） |
| 页面重载 → 首条数据 | 2–6s | dsh/页面 |

### 等待点一：spawn 被窗口初始化串行化

`ShellApplication` 在 `window.Opened` 后才调用 `_startup.Begin(...)`；`ShellStartup` 随后才跑 profile/companion 前置与 `RuntimeStarter.StartAsync`。代理在进程启动后约 1s 已绑定，`Opened` 之前没有任何步骤依赖 dsh。窗口与 WebView 初始化按日志间隔推断为 1–3s【推断 · 未证：日志无毫秒级分界】，这段时间本可让 dsh 并行启动。

这是既有决定：Avalonia G0 明确「窗口先亮 + `Opened` 后 `ShellStartup.Begin`」，G1/G3 的监督篇沿用同一触发点。本提案保留「窗口先亮」，只改 spawn 的起始时点。

### 等待点二：2s 稳定窗实际跑成 5–7s

`DshShellForward.WaitWebFaceStableAsync` 每轮串行执行 `POST /api/session/list`（冷启约 2.4s）与 `/api/remote.mux` 升级探测：

1. 第 1 轮结束才把 `_readySince` 设为当前时刻；
2. 再等 1s 节拍；
3. 第 2 轮再做一次完整 `session/list`（又约 2.4s）；
4. 到第 2 轮结束，`now - _readySince` 才满 2s。

因此成本主体是**两次冷启 `session/list`**，不是 2s 窗口本身。该判据来自 [mint-epoch-mux-gate](../../implemented/bug-fix/2026-10-02-mint-epoch-mux-gate.md) 与 [relay-restart-client-module-collapse](../../implemented/bug-fix/2026-09-19-relay-restart-client-module-collapse.md)：单次 Ready 可能撞上将死的前驱，必须连续维持。收窄必须保留「会话面 + 通道面同时 Ready」与 fail-open，不得退回单次 Ready 放行。

### 等待点三：代理丢弃缓存响应头，旧 SPA 页面在未铸币窗口发请求

502 的 5 条请求路径是 SPA 首页的子资源（`index-*.js/css`、`vendor-*.js/css`、`plugins/...client.js`），到达时刻与 holder 页同一秒：

| 启动 | holder 页 | 5×502 | dsh 就绪 |
|---|---:|---:|---:|
| 09:07 | 09:07:00 | 09:07:15 | 未等到（窗口已隐藏） |
| 10:35 | 10:35:22 | 10:35:37 | 26.8s |
| 14:48 | 14:48:45 | 14:49:00 | 19.9s |
| 14:51 | 14:51:21 | 14:51:36 | 22.1s |

`DshLoopbackProxy` 在未铸币时最多等 15s，超时才 502；但冷启动的铸币放行 = dsh 就绪 + 稳定化，本样本全部晚于 15s。也就是说，**任何在铸币前到达的代理转发子资源都必然失败**。

根因在代理回包：`RelayTerminalAsync` 只保留 `Content-Type`，`WriteBufferedAsync`/`WriteStreamHeadAsync` 只写 `Content-Type`、`Content-Length`、`Connection`；dsh 的 `Cache-Control`、`ETag`、`Last-Modified` 一律被丢弃。实测直接访问 dsh `/` 的 401 带 `cache-control: no-store`，经代理的 200 HTML 与静态资产都只剩 `Content-Type`。WebKit 的持久缓存（243MB）中因此存在 `http://localhost:33625/` 的 `200 text/html` 条目且无 `Cache-Control`；冷启动时旧文档被重放（推断），其子资源撞上未铸币窗口。机制上高度吻合，但尚未用 `DSH_DESKTOP_PROXY_TRACE=1` 做一次闭环复现【推断 · 未证：重放触发点是缓存启发式还是 WebKit 恢复会话，未分型】。

同一缺陷还使带 hash 的静态资产失去 `immutable`（旧直连缓存记录仍有 `public, max-age=31536000, immutable`），页面重载失去显式缓存依据，是否重取交给浏览器启发式。loopback 代理 ADR 已把「响应头最小集」留成 TODO，本提案承接该项。

### 不解决的部分

dsh 的 14–27s 主要来自 desktop profile 的 12 个 bundle 与插件树加载（dsh 侧无分型日志，【推断 · 未证】），壳无法在不改 profile 的前提下压缩；本提案只压缩壳侧可归因的 5–8s（探索性估计）与 502 噪声，不宣称把冷启动降到 10s 内。

## Proposal

按优先级分三档，允许独立落地：

1. **spawn 前置（P1）**：单实例仲裁与代理绑定完成后，在窗口构造前触发 `ShellStartup.Begin`；窗口仍按原时点显示。启动失败事件在窗口未就绪时先缓冲，窗口与失败面接好后补展示，避免「后台已失败、UI 永远不弹」。
2. **铸币稳定化收窄（P1）**：每轮并发探测会话面与通道面；第 2 轮只复探通道面（或复用第 1 轮会话结果）；探活本身已耗满稳定窗时不再追加 1s 节拍。保留双判据、2s 连续窗与 30s fail-open；目标把 5–7s 压到 4s 左右。
3. **代理缓存响应头白名单（P2）**：回包只透传 `Cache-Control`、`ETag`、`Last-Modified`、`Expires`，继续永不透传 `Set-Cookie` 与 hop-by-hop 头；holder 页补 `Cache-Control: no-store`。目标是消除 4/6 次启动各 5 条、各等满 15s 的 502，并恢复静态资产的显式缓存。
4. **启动期小项（P3，可独立）**：`FirstBootBootstrapService.Resolve` 的同步 `dsh --version` 结果供 `StartupNoticeService` 复用，去掉第二次探针；启动更新检查推迟到首屏后，失败时留一行原因而不是静默回到 `Idle`。

落地顺序：P1 两项可同批（都改启动链时序）；P2 独立、风险最低；P3 随后。每项落地时把对应 implemented ADR 的旧表述同步（见 Related）。

## Alternatives considered

- **把未铸币等待预算从 15s 抬到 60s（覆盖 dsh 冷启）**：落败。它只把快速 502 换成长时间挂起，旧页面的错误请求仍存在，还占用连接；不解决缓存头丢失。
- **启动时清空 WebKit HTTP 缓存**：落败。243MB 持久缓存全清代价高、丢资产缓存，且是钝器；白名单透传只恢复本该保留的缓存头。
- **删除 2s 稳定窗，单次 Ready 即放行**：落败。Mint gate 的既有实机证据表明单次 Ready 会撞上「UI 先出、树晚 ~15s」的渐进渲染；收窄采样组织可以，判据不能退。
- **把所有 dsh 响应头原样透传**：落败。会带回 `Set-Cookie` 与 hop-by-hop 头，破坏「壳代持 cookie、页源零透传」的安全不变量；只取缓存白名单。
- **进程一启动就 spawn，不等单实例仲裁**：落败。第二实例会先拉起 dsh 再退出，制造孤儿与端口竞争；spawn 必须在单实例与代理之后。
- **先删 profile 的 12 个 bundle 以换启动速度**：本批不取。功能取舍属产品决策，且删插件会改变用户能力面；另立评估，不在启动链修复里顺手做。
- **只把窗口提前显示、仍等 `Opened` 再 spawn**：落败。窗口初始化本身就是被等待的那段，提前显示不改变 spawn 时点。

## Acceptance criteria

- n≥3 次冷启动中，启动期 `代理未铸币即调用：502` 为 0；`DSH_DESKTOP_PROXY_TRACE=1` 下不再出现旧 SPA 子资源突发。
- 同一组冷启动中，`dsh web 就绪 → 稳定化放行` 的中位数 ≤4s（目标，非承诺）；spawn 日志早于窗口可见（`无边框顶栏启用` 行）。
- 冷启动总交互时间相对本基线中位数下降 ≥5s（同机、同 profile、n≥3；记录每段耗时）。
- spawn 在窗口前失败时，窗口仍能显示失败面；关窗/退出路径不因提前 spawn 留下孤儿 dsh。
- 经代理的 HTML 响应可见 `Cache-Control: no-store`（或 dsh 原值），静态资产可见 `Cache-Control`（及 dsh 提供的 `ETag`/`Last-Modified`）；`Set-Cookie` 仍不出现在页面响应。
- 回归：`ShellStartup`/`ShellRecovery` 相关测试、`DshShellForward` 稳定化测试（虚拟时钟）、代理回包头测试；`verify-compose-root`、`verify-md-links`、`verify-adr-format` 全绿。

## Risks

- **失败面时序**：提前 spawn 后，`Failed` 可能早于窗口与 `WireStartupFailure` 订阅；必须缓冲并只展示一次，否则失败会静默。
- **缓存头白名单**：白名单一旦放宽到 `Set-Cookie` 或 `*`，会把壳 cookie 暴露给页面；实现与测试都要钉住「只白名单、永不 cookie」。
- **稳定化收窄**：只复探 mux 可能漏掉会话面在窗口内再次掉线的形态；若实机出现渐进渲染，回退到双面复探并保留 2s 窗。
- **测量强度**：本基线是单机 n=6 的探索性数据，改动后的收益需同机复测，不以单次体感结账。
- **profile 主导**：即使 P1/P2 全落地，dsh 插件树仍占 14–27s；对外表述不得承诺「秒开」。

## Related

- [avalonia-g0-entry-switch](../../implemented/architecture/2026-10-22-avalonia-g0-entry-switch.md)：其「`Opened` 后 `ShellStartup.Begin`」的次序由本提案部分取代；「窗口先亮」不变。落地时更新该篇的 Decision 与 Consequences。
- [avalonia-g1g3-recovery-bootstrap-supervision](../../implemented/architecture/2026-10-23-avalonia-g1g3-recovery-bootstrap-supervision.md)：同一触发点的监督接线；落地时同步。
- [mint-epoch-mux-gate](../../implemented/bug-fix/2026-10-02-mint-epoch-mux-gate.md) 与 [relay-restart-client-module-collapse](../../implemented/bug-fix/2026-09-19-relay-restart-client-module-collapse.md)：稳定化判据的来源；本提案只改采样组织，双判据与 fail-open 保留。
- [loopback-forward-proxy](../../implemented/architecture/2026-09-27-loopback-forward-proxy.md)：其「响应头最小集」TODO 由本提案 P2 承接。
- [runtime-starter-core-usecase](../../implemented/architecture/2026-10-08-runtime-starter-core-usecase.md)：起步用例的端口与测试面；实现时沿用，不重写。

以上 implemented 篇均为部分承接：没有整篇被取代，故本批不归档任何笔记；落地时按各篇逐条更新 Decision/Consequences，而不是新建重复篇。
