# Agent Note: mint-epoch-mux-gate（重启「UI 先出、树晚 ~15s」根因——通道面就绪 + 铸币 epoch 化）

Status: implemented

Review: FULL/2026-10-02/R1=ok R2=ok R3=ok

## Problem

0.5.15 实机（`~/.dsh/logs/host.log`）四次市场重启全部同一模式，无一例外：
页面自刷落进新进程后 UI 框架与第一波 API 全部 200，但 `/api/remote.mux` WebSocket 升级
「零字节即关」，**固定 ~15–16s 后**重试才 101 成功，且 `POST /api/session/list`（树数据）
恒在 101 成功的同一秒才发出——树比 UI 晚 15–20s。四次事件：09-30 01:26:05→21、
10-01 00:43:11→27、10-01 13:35:21→36、10-02 00:13:24→39。冷启动（00:07:50 首连即 101）
与浏览器无此象：前者是 holder 稳定化**恰好**把页面到达推迟过了窗口（时序掩盖非结构免疫），
后者没有壳收养链路。

根因三层（上游源码逐行 + 壳侧结构）：

1. **上游启动窗口**：`dsh-api-gateway` 的 `/api/remote.mux` 升级路由要等 `appReady` 才注册
   （`webCtx.get("appReady")` → `onReady(listen)`），而 unary RPC 与静态面早已服务；
   `dsh-host-webserver` 的 upgrade 处理器对无路由升级直接 `socket.destroy()`——零字节、
   无任何 HTTP 错误；`dsh-client-connection` 的连接代际就绪超时 `generationReadyTimeoutMs=15e3`
   即那 15 秒的出处。
2. **壳判据缺口**：铸币稳定化只探会话面（session/list ok:true，c7b1565 后形态），
   看不见通道面——门控钉不住「页面可用」。
3. **壳铸币态单调**：`_mintedTcs` 一次性永决 + route 不清 → 重启窗口里页面自刷绕过 holder
   门控，落到指向已死进程的 502 或半成品页；且收养重铸走旧 token URL 恒 401
   （续任者 per-process token 轮换，4/4 实测；cookie 因落盘持久密钥按 authority 绑定仍有效，
   故转发面未断——潜伏断裂）。

## Decision

1. **稳定化判据复合化**：Ready = 会话面（`POST /api/session/list` 200 + `result.ok:true`）
   **且** 通道面（`GET /api/remote.mux` 升级完成 101 握手）。零字节销毁/401/超时一律未就绪。
   铸币（`MintAsync`）与收养重验共用。探活为**裸 TCP 单连接**握手（不走 ClientWebSocket：
   其连接池对被掐断的升级请求有内部重试，实测一次探活派生 ~4 条内核连接——探活须是行为良好的
   单连接客户端，不给半死的 dsh 添连接风暴；多客户端并发探测的同理）。
2. **铸币态 epoch 化**：新增 `InvalidateRoute()`——route 清空（代理 `/` 立即回落 holder、
   其余 502）+ 就绪门重新武装（新门由后续铸币/重验放行）；cookie 保留作重验凭据。监督面在
   恢复周期起点（`ShowRecoveryPageAsync`，子进程退出后、重启等待前）调用——重启窗口里的
   页面自刷从此被门控吸收。**幂等**：已处失效态（route 空且门未决）即 no-op——残留锁死分支
   逐轮重入不得反复换门（旧草案逐轮替换未决 TCS 会取消 holder 已在等待的长轮询、打熄其
   有界重试链，R2 验收收口）；失效翻转留痕只在首轮。**落定原子**：route 写入与就绪门置结果
   同临界区（与失效的锁内换门互斥，锁外置结果与失效并发即「无 route 放行」，R2 验收收口）。
3. **收养重验 token-free**：新增 `RevalidateAsync(url)`——有存 cookie 即对续任者 origin 复合
   探活，稳定即重指 route 并放行（不发 303）；无存 cookie 回落 `MintAsync`（壳自 spawn 的
   URL 带新 token 仍可用）。收养导航（`NavigateAfterAdoptAsync`）改走此路径。
4. **夹具**：`DshMimicResponder` 补通道面（101 握手 / `-1` 零字节销毁 / 无 cookie 401）；
   `StubWebSocketServer` 改循环受理——探活升级会先打到该桩且被其严格判门 403（真实 dsh 门
   仅拒 cross-site、Origin 缺省放行，探活可过；桩比真门严是判门断言所需），单连接形态会被
   探活抢占而饿死页面连接。

## Alternatives considered

- **上游修（mux 路由提前注册 / 无路由回 503）**：rejected——上游发版周期不受本仓控制；
  「unary RPC 先行、mux 等 appReady」是有意设计，壳侧 holder 门控是其等价吸收位，自洽即可。
- **收养探测（`ProbeLoopbackWebAsync`）同步加深到通道面**：rejected——收养判定（收养 vs
  竞争 spawn）不需要页面级就绪；holder + 重验已把页面到达钉在复合就绪后，加深收养探针只会
  推迟收养登记（退出时整树收割的覆盖窗随之推迟）。
- **失效时机改「监督器重启成功后」**：rejected——窗口里页面自刷已发生，门控必须在进程死
  即刻武装，否则自刷先落 502/半成品页。
- **重验复用 303 token 铸币 + 失败重试**：rejected——token 每轮换恒 401（4/4 实测），
  重试只是把白等换个名字。
- **页面级 DOM 注入探树渲染当判据**：rejected——renderer 注入是新边界面（R3 成本脆度高）；
  mux 101 已是页面可用（树数据面）的充分先行量，5/5 事件「101 同秒 session/list」实证。

## Consequences

收益：重启收养后 holder 撑到「会话面+通道面」复合就绪才放行，首屏一次出全（UI+树同帧）；
收养重铸不再 401；页面自刷不再落 502 错误页。冷启动的门控从时序运气变结构保证。

代价：铸币/重验放行时刻后移到 appReady（冷启 holder 时长 +数秒，全内容到达实测提前）；
探活形状与上游 mux 路由/门契约耦合（路由改名或门收紧即漂移——由夹具钉死在 CI 显形，
不再靠「HTTP 200」弱证据）。

遗留（归上游面）：页内自举残余——方向与推动状态单一家见
[holder-mint-gate-deepening](../feature/2026-10-01-holder-mint-gate-deepening.md)
Alternatives 的 archived 行（上游列表快照 stale-while-revalidate），本笔记不另登记。
`ProbeLoopbackWebAsync` 三态仍只钉 HTTP 面（收养登记时刻 ≠ 页面可用时刻），由 holder 门控兜住。

## Testing

- 新增 5 条回归：复合门等待通道面（通道面未就绪前 route 不可见 + 稳定窗放行非 fail-open）、
  通道面恒不就绪 fail-open（零字节销毁形态钉死）、`InvalidateRoute` 幂等（首态/重复失效
  no-op 不扰未决等待）+ 翻转 + 重武装 + 再铸币放行、重验 token-free（不再走 303）、
  重验无 cookie 回落铸币。
- 代理 holder 测试补 epoch 钉子：失效后 `/` 回落 holder（页面自刷被门控吸收）；
  组合根序列测试补接线钉：恢复周期起点必失效铸币态（删调用即红）。
- 压测：夹具桩在探活风暴下曾饿死页隧道连接（per-connection socket 异常冒出受理循环即停服 +
  串行受理被灌满 + 连接所有权双重释放三处叠加），修复后同类 8/8 全绿且时长稳定。
- `dotnet test` 917/917 全绿 0 警告；基线/README 双语徽章同步。

## Related

- [holder-mint-gate-deepening](../feature/2026-10-01-holder-mint-gate-deepening.md)（门控决定出处；本批 Decision 3 后收养重铸常态改走 token-free 重验，其「四条铸币链同走 MintAsync」的口径中收养链降为无 cookie 回落形态）。
- [mint-probe-payload-envelope](./2026-10-01-mint-probe-payload-envelope.md)（会话面探活形状；本批把判据加深为复合面）。
- [adopt-skip-navigate-on-self-reload](./2026-09-25-adopt-skip-navigate-on-self-reload.md)（页面自刷免导航——本批让自刷落进门控而非半成品页）。
- [relay-restart-client-module-collapse](./2026-09-19-relay-restart-client-module-collapse.md)（重启窗口事故出处；稳定窗判据同源）。
- [upgrade-tunnel-host-authority](./2026-09-27-upgrade-tunnel-host-authority.md)（升级隧道留痕与门语义；本批探活经同一隧道语义直连 dsh）。
