# Agent Note: 升级隧道握手补 Host 权威值（过 dsh 网关 Host/Origin 门）

Status: implemented

Review: LIGHT/2026-09-27/R2=ok

R2 处置：首轮 1 Blocker（页源 `Referer` 透传）+ 5 Suggestion 全收——Blocker 的 403 机制经补探测**证伪**（`Referer` 不在 dsh 门上），但"页源源值零透传"不变量确不成立，按对齐普通转发面改写；修复轮复审 0 Blocker、6 条处置逐条确认成立，另 2 条文档面 Suggestion（证据标注强度、黑名单口径措辞）亦收。

Related: 前序 [`architecture/2026-09-27-loopback-forward-proxy`](../architecture/2026-09-27-loopback-forward-proxy.md)（本隧道面与其页源 Origin 改写同源；本篇更正其手术清单缺项）+ [`bug-fix/2026-09-27-upgrade-tunnel-watcher-exemption`](2026-09-27-upgrade-tunnel-watcher-exemption.md)（同一条 `remote.mux` 线的上一轮；其"隧道存活回归泵两端自然收敛"在该轮未真达成，因隧道从未过门）

## Problem

- **现象**（Linux 实机，v0.5.7 `2026-09-27 21:13` 自更新后，38 分钟窗口，`~/.dsh/logs/host.log`）：`remote.mux` 隧道建立 331 次，其中 329 次**同秒即收**（余 2 次 1 秒），`先关方`恒为 dsh；dsh web 客户端每 5–10 秒重试一次（间隔抖动，近似退避）。客户端 remote 流通道始终不可用。
- **机制（已证，代码面）**：`DshLoopbackTunnel.BuildUpgradeHead` 跳过页面传来的 `host` 且不写权威值（原注释"`Host` 由 TCP 目标隐含，不伪造"）。该假设对普通转发面成立——`DshShellForward` 走 `HttpClient`，Host 按 URI 自动补；对这里的**裸 TCP 重放不成立**，HTTP/1.1 请求头不会自己出现。
- **门（同形探测；每种形态 n=1，【探索性】）**：对活体 dsh 发与隧道完全同形的握手，逐组只变一个头：

  | 请求形态（其余头同隧道形态） | dsh 应答 |
  | --- | --- |
  | 无 `Host` + `Origin` 自源 + `same-origin`（**隧道的原形态**） | `403 forbidden`（体 9 字节） |
  | `Host: 127.0.0.1:<port>` + `Origin` 自源（无 cookie） | `401 unauthorized`（体 12 字节，只差 cookie） |
  | `Host` 对 + `Origin` 页源 | `403 forbidden` |
  | `Host` 页源值 + `Origin` 自源 | `403 forbidden` |
  | 无 `Host` 无 `Origin` | `403 forbidden` |
  | `Host`/`Origin` 对 + `Referer` 取页源 / 自源 / 外源三种 | 全 `401 unauthorized`（门**不判** `Referer`） |

  即 dsh 网关**先判 Host/Origin 门（两者都须是 dsh 自己的 authority/源），再谈 cookie 认证**；`Referer` 不在该门上。
- **因果（【推断 · 未证直接观测】）**：隧道被该门 403、因而从未升到 101 → 代理把 403 原样泵给页面 → 客户端退避重连 = 上述 331 次建/收的来源。代理不记升级面的上游响应行（"已建"在读到响应前落盘），故这一环没有直接抓取证据；旁证是时延（同秒）、关闭方（恒 dsh）与原形态探测结果三者一致。
- 影响面：所有经代理的 WS 升级（当前即 `remote.mux`）到不了 101，dsh 客户端 remote 流通道不可用。这是 v0.5.7 新开的传输面自身的缺口，与鉴权/铸币无关（同期普通转发请求全 200）。

## Decision

- `BuildUpgradeHead` 写 `Host: <dsh authority>`（由路由 `Uri.Authority` 派生），与 `Origin`（`Uri` 的 authority 左部）**同源派生**。手术清单由此变为：`Host`→dsh authority + `Origin`/`Referer`→dsh 自源 + 贴 cookie + `sec-fetch-site: same-origin`，其余头原样。
- `Referer` 与普通转发面同法处理（`DshShellForward` 构造请求时 `Origin`→authority、`Referer`→dsh 形目标）：页源值丢弃、改写为 dsh 自源 + 原目标路径。该头**不在** dsh 的门上（见探测表末行），改写只为"页源源值零透传"这条两个面共用的不变量——隧道面此前漏了它。
- 页源 `Host`/`Origin`/`Referer` 三值零透传；其余头（`Sec-WebSocket-*` 等）仍原样透传。两传输面只在**源值改写**这一条上对齐，黑名单口径并不相同：隧道面额外丢 `sec-fetch-site`（重写为 `same-origin`），也不丢 `accept-encoding`（WS 握手不带该头；普通面丢它是为避免 gzip 字节直送页面）。
- 测试桩 `StubWebSocketServer` 按真实 dsh 判门：过门才回 101，缺 `Host`/`Origin` 非自源即回 `403 forbidden`（体 9 字节）+ 关连接——桩不再对 Host 失明（这正是本缺口逃过上一轮测试的原因）；首部按 `CRLFCRLF` 收满再判（单次 `ReadAsync` 遇 TCP 分段会假红）。

## Alternatives considered

- **`Host` 透传页源值**：落败——页源是代理源（`localhost:<proxy port>`），dsh 按同门 403（探测表「`Host` 页源值 + `Origin` 自源」行）。
- **只补 `Host`、`Origin` 沿用页源**：落败——探测表「`Host` 对 + `Origin` 页源」行 403；两个值必须同源于 dsh authority。
- **只修 `Host`，`Referer` 维持透传**（本轮 R2 评审提出的分界）：落败——`Referer` 不上门（三种取值实测均只到 401），透传不会引发 403，但两个传输面会给出不同形态的源，且"页源源值零透传"这条不变量在升级面上破例；改写成本一行，取一致。
- **升级面改用 `HttpClient` 或自行解析上游响应头**：落败——升级后是裸字节流，`HttpClient` 不承接 101 长连接；解析响应头会把"字节透明"的泵改成有状态转发，风险大于收益。上游响应留痕另计（见 Deferred）。
- **不写 Host，靠连接目标隐含**：落败——HTTP/1.1 头不会自动出现（原注释的假设本身就错）；普通面无此问题只因 `HttpClient` 代劳，不能援引。

## Consequences

- 收益：`remote.mux` 可达 101 并与连接同寿；两传输面对源头的处理归一（`Host`/`Origin`/`Referer` 同源于 dsh authority），信任面不变——仍只贴 authority 值 + cookie。
- 代价：两行头，无新增状态、计时器或线程；`DshLoopbackTunnel` 体积不变。
- 剩余缺口（Deferred）：升级面仍不记上游响应行，"已建"仍早于响应落盘——同类问题下次仍要靠同形探测或抓包定位，日志自身说不了"谁拒的、什么状态"。

## Testing

- `Proxy_UpgradeTunnelsWithHeaderSurgery` 增设断言：桩收到 `Host: 127.0.0.1:<stub port>` 与 `Referer: http://127.0.0.1:<stub port>/api/remote.mux`，且页源的 `Host: x` / `Referer: http://localhost:12345/page` 均不透传；桩补判门后，`Proxy_UpgradeTunnel_SurvivesClientFrames` 一并获得门覆盖。
- 旧码复演：仅回退 `DshLoopbackTunnel.cs`、保留新测试 → 两例即红（159ms，首字节为 403 而非 101）；新码 Infrastructure 套件全绿。
- 实机复验（2026-09-27 22:21，装机重启，v0.5.8）：`host.log` 自重启起 `remote.mux` **只建 1 次、0 次收**（22:21:36 建，存活至本行记录时）；修前该窗口是每 5–10 秒一对（38 分钟 331 建 / 329 收），客户端退避重连循环停止——即"因果"段推断的预测（过门即长存）成立。
