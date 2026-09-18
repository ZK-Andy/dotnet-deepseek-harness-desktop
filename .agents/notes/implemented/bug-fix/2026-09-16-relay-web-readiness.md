# Agent Note: relay-web-readiness（接力就绪判据升级为 web 面可服务）

Status: implemented

Review: FULL/2026-09-16/R1=ok R2=ok R3=ok

## Problem

市场更新插件触发一键重启后，壳的接力收养路径仍会让用户看到长时间白屏。实机取证（v0.5.2，Linux，2026-09-16 04:51，n=1【探索性】）host.log 时间线：

| 时刻 | 事件 |
|---|---|
| 04:51:21 | 市场记 `restart scheduled pid=8805 helper=78970` |
| 04:51:23 | 监督器看到在管 dsh 退出（市场 helper 已杀进程），进入恢复 |
| 04:51:33 | 接力命中：「市场接力续任者已接管首选端口 35263」→ 收养 pid 78977 → 「重启成功」 |
| 04:51:34 | 导航到达（第 1 次导航） |
| 04:51:56 | 页面探针连续 3 次为空（dead）→ 触发有界恢复 reload #1 |
| 04:52:06 | 页面健康 alive（probes 798） |

不可用约 43s，其中页面真正空白约 30s，且**两次导航**——与 [市场接力收养优先](../feature/2026-09-15-market-restart-adopt-first.md) 承诺的「单进程、单导航」不符（单进程成立，单导航不成立）。

机制（已证）：接力判据 `RuntimeLineageProbes.IsLoopbackServingAsync` 只做一次 TCP `ConnectAsync`（源码注释原话「TCP 连接成功即视为有人服务」），而 dsh 的启动序是**先 bind 端口、后挂 web 面**——`dsh-web-app/cordis.patch.yml` 中 `webserver` 行先于 `web-runtime` 行（后者才挂认证、前端静态并打印 URL）。本机就绪态 HTTP 形状实测（n=1【探索性】）：`/` 与 `/index.html` 返回 `401` + 68B 文案体、`/api/health` `401` + 12B；未挂载形态是空体（`/nonexistent-xyz` → `404` size=0）。壳在「只有 TCP 在听」的时刻就导航，WebView 拿到空文档；页面健康看门狗的保守去抖（10s 间隔 × 连续 3 次，见 [页面健康观测](../process/2026-08-26-page-health-monitor.md)）要 ~22s 才宣告 dead，再过 ~10s（reload + 下一拍探针）才 alive。

耗时分解【推断 · 未证】：43s ≈ dsh 自身启动 ~10s + 看门狗去抖 ~22s + 恢复拍 ~10s。首次导航后的空窗究竟由 dsh 尚未可服务还是 WebView 侧加载竞态造成，日志无法区分——就绪判据升级对两种成因都收紧，故不需要先分辨。

## Decision

接力等待窗口的就绪判据从「端口可连」升级为「**web 面已应答**」，新增三态探针 `RuntimeLineageProbes.ProbeLoopbackWebAsync`：

- `NotServing`：TCP 连接失败（续任者尚未 bind）；
- `ServingNotReady`：TCP 可连但 HTTP 无声（空体/超时）——web 面尚未挂载，**继续等，不导航**；
- `Ready`：HTTP 响应带响应体，或 3xx 重定向（无 cookie 的裸请求可能被引导到带 token 的 URL，重定向同样证明路由与认证已挂载）。

`HarnessRuntimeHost.TryRideMarketRelayAsync` 只在 `Ready` **且该就绪连续维持满稳定窗（2s ≈ 连续 3 拍，见 [relay-restart-client-module-collapse](2026-09-19-relay-restart-client-module-collapse.md)）** 时走收养链；`ServingNotReady` 时按原节拍继续等待，并在决定继续等待后留痕一条「已监听但 web 面未就绪」（每次等待只记一条，防 1s 节拍刷 host.log；取消不留痕——那时并未继续等）。探针单次 HTTP 超时 1.5s、不跟随重定向、环回不走代理；整次探测按**剩余预算**收紧（TCP 预检与 HTTP 段共用同一预算，实际等待取剩余预算与 1.5s 的较小者），故单轮迭代不会把等待拖过总预算；任何探测异常折算为「未就绪」，绝不因探测失败判死。等待的三条退出线（证据消失 / 总预算 / helper 从未出现的宽限窗）与收养、收割、漂移处置全部不变，预算耗尽仍回落既有路径（其 bind 预探测兜底）。

同批撤除 `RelayServingProbeOverride` 注入口：它唯一的用途是替掉 TCP 判据，而真实探针已由 Linux 假进程用例证明可用——正是 HANDOFF 待办「接力等待注入口可撤其一」写明的触发条件（「下次动该文件」）。就绪判据如今有专门契约用例，注入口不再有消费者。

## Alternatives considered

- **缩短页面健康看门狗去抖（间隔或阈值）**：落败——它同时服务所有白屏形态，收紧等于把「误报 → reload 循环」的风险摊到全部场景，而该阈值保守是有意为之（误报重载循环比白屏更伤可用性）。本决定不动看门狗语义，只在接力命中前把「尚未就绪」挡在导航之外。
- **市场侧改 blue-green（helper 保留旧 dsh 直到续任者就绪）**：落败——依赖上游产品行为与发布节奏，壳侧单边修复自足（与 market-restart-adopt-first 的同类备选一致落败）。
- **用带 cookie 的 HTTP 请求探 SPA 真页面**：落败——会话 cookie 在 WebView 的 jar 里，壳拿不到，也不该去伪造凭据；`401` + 68B 文案体已足以证明「认证 + 前端路由已挂载」。
- **只修文档、不改代码**：落败——40s 级白屏是用户可感缺陷，文档修正不能消除它；文档修正作为跟车项执行（见 Related）。

## Consequences

- 恢复期间用户看到的是**恢复屏**而不是空白页；接管命中（导航）推迟到 web 面就绪**并连续维持满稳定窗**，导航次数回到 1 次，不再依赖看门狗 reload 兜底。
- 代价：若续任者 web 面迟迟不就绪，壳会等满接力预算（与 `RestartAsync` 恢复时限同源）再回落既有路径——比现状多等，但多等期间页面不白，且原路径的 bind 预探测兜底不变。
- 观测面：新增 `[host] 市场接力续任者端口 … 已监听但 web 面未就绪：继续等待，不导航进空白页`；接管命中行在既有前缀后追加稳定窗措辞（`[host] 市场接力续任者已接管首选端口 <port>（就绪连续维持 ≥2s）：跳过竞争 spawn，转入收养处置`），前缀与既有判读口径不变。
- 就绪判据依赖 dsh 的 HTTP 契约（裸请求有响应体或 3xx）：dsh 若改成空体 200 的「就绪」形态，探针会误判为未就绪——该契约留档于探针的 XML doc 与本文。

## Testing

- 探针契约（`RuntimeLineageProbeTests`，夹具 = `LoopbackHttpResponder`）：无人监听 → `NotServing`；只监听不应答 → `ServingNotReady`（300ms 预算上界）；带响应体 → `Ready`；3xx 重定向 → `Ready`；空体应答 → `ServingNotReady`。
- 接力窗口（`HarnessRuntimeHostTests`，Linux 真 `/proc` 探针）：helper + 续任者证据在场、端口只监听不应答 → 等满预算返回 null 且留痕未就绪；端口按 HTTP 应答 → 一次收养；窗口内取消 → 返回 null、不收养、不留假就绪行。
- 实机复验（2026-09-16，v0.5.3 装机后随市场插件更新触发；Linux，n=1【探索性】）：`06:05:39` 在管 dsh 退出 → `06:05:49` 留痕「已监听但 web 面未就绪：继续等待」 → `06:05:50` web 面就绪即收养（续任者 pid 124500 接管 35263）→ `06:05:51` 壳侧一次导航。该窗口内「连续 3 次探针为空」「触发有界恢复 reload」「漂移至」命中数均为 0（`06:05:39` 起全量行核对）。对照修复前同机同场景（`04:51`）：43s 不可用、看门狗 reload 一次、两次导航。附注：本轮另观测到一次**页面自发** reload（旧 dsh 客户端在续任者就绪后自恢复，日志时序早于收养行），属页面行为、不是壳侧导航。

## Related

- [市场接力收养优先](../feature/2026-09-15-market-restart-adopt-first.md)：本决定收紧其就绪判据（原按端口接管即收养），同批同步该文的事实表述。
- [bind 预探测压缩端口等待](../architecture/2026-09-13-port-wait-compression.md)：其「dsh 加载整树才 bind」的测量正是「端口可连早于 web 面可服务」的前提。
- [收养市场接力的 dsh 续任者（运行时交接）](2026-09-12-runtime-handoff-adoption.md)：本决定只改「何时判定接手」，收养判据、收割面保护与判活 token 复验均以彼为准。
- [页面健康观测](../process/2026-08-26-page-health-monitor.md)：本轮兜底恢复的提供者；其去抖代价是本决定要避开的。
- [relay-restart-client-module-collapse](2026-09-19-relay-restart-client-module-collapse.md)：本决定的三态就绪判据之上加「连续维持」稳定窗（单拍 `Ready` 可能是将死前驱的应答）。
