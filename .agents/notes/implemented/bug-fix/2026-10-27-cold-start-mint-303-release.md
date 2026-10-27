# Agent Note: 冷启动铸币 303 即放行

Status: implemented

## Problem

冷启动的“铸币→页面可用”之间存在一段纯壳侧等待：`MintAsync` 在 303 铸到 cookie 后先做复合探活
（`POST /api/session/list` + `GET /api/remote.mux` 升级），要求两面同时 Ready 且连续维持 2s 才写 route、
放行 holder 的 `/__shell_ready`。本机 `host.log` 实测（2026-10-26/27，Linux x64，n=8 次冷启动）：
铸币完成到满窗 **5–9s（中位 7s）**，每次都恰好 `探活2次`；而这段等待之后，页面自己还会再发一次
`session/list`（2026-10-27 11:20 实测 2909ms）。即：探活不减少页面首取，只是在它前面加了一轮同型 RPC。

上游 `apps/desktop` 的形状相反：Host 经 IPC 在 `runProfile` 完成后发 `ready`，Electron 单次 303 换 cookie
即 `loadURL`，没有任何就绪探活；页面晚挂载由渲染进程的 boot 等待与客户端重连收敛。

## Decision

**冷启动铸币在 303 即放行，不再前置就绪探活。**

- `MintAsync` 收到 303 + `Set-Cookie` 即在同一临界区写 `_authority`/`_cookieHeader` 并放行 `_mintedTcs`
  （与 `InvalidateRoute` 的换门语义一致）；日志记一行“route 即放行”。
- 会话面/通道面探活只保留在**收养重验**（`RevalidateAsync`）路径：那里有将死前驱仍会应答的真实风险，
  2s 稳定窗与双面判据原样不动（ADR `relay-restart-client-module-collapse`）。
- 失败语义不变：非 303 或无 cookie 仍不写 route、不抛；铸币异常照旧脱敏 loud。

## Alternatives considered

- **保持现状（阻塞到稳定窗）**：实测 5–9s，全部排在页面加载之前；放弃。
- **单轮判据（只探一次双面即放行）**：实现并跑通单测后放弃——它仍要付一轮 `session/list`（约 2.4s），
  而结果没有任何消费方：页面随后自己的 `session/list` 才是真正取树的那次，壳侧判据只决定“何时 reload”。
  既然放行与否不再改变页面行为，这一轮 RPC 就只剩成本。
- **稳定化转后台做遥测**：放弃。没有消费者（监督器判活看进程，健康探针已删除）；要为它保留单轮分支、
  后台任务生命周期与日志并发，还要把 36 处测试的日志断言改成并发安全，收益只是一行 log。
- **只删 2s 稳定窗、保留单轮探活**：放弃。省下的是窗口，不是 RPC；实测里窗口的增量接近零，主要成本是探活往返。
- **把探活改成页面上报（桥注入心跳）**：放弃。等于重建已删除的健康探针，且要侵入 dsh 页面自身的状态。

## Consequences

- 冷启动从“spawn→URL→稳定化→页面”变为“spawn→URL→303→页面”，壳侧固定开销由 5–9s 降到一次 303 往返。
- **首帧可能早于会话树**：页面在 dsh 的 `appReady` 之前就开始加载；若客户端不重连，症状是 UI 在、树空/通道断。
  壳侧没有页面状态信号，无法自动判定（健康探针已按 `2026-10-24-drop-page-health-polling` 删除）。
- 未铸币 502 窗口同步缩短（route 在 303 即成立），启动期 502 条数不作为本决定的判据。
- 收养路径的测试覆盖整体保留：原 `Mint_*` 稳定化断言迁到 `Revalidate_*`；铸币侧新增“零探活、route 即见”。

## Risks

- **树晚到的旧证据**：`holder-mint-gate-deepening` 记录单次 Ready 会落进“UI 先出、树晚约 15s”。本决定不再等；
  若实测复现且不自愈，应回退到单轮判据（约 2.4s），而不是恢复 5–9s 的双轮稳定窗。
- **验收口径**：n≥5 次冷启动，记录 URL→页面开始加载、首帧→树可见、启动期 502 条数、是否需要手动 reload；
  判负即回退。数字回填前，本决定的收益只有单次日志样本支撑（探索性）。
- **终页裁决与 holder 抢跑**：`MainUiEntry.SettleWebSessionAsync` 的探针与 holder 的 reload 现在同刻起跑；
  探到 holder 即判 Unknown 并提前返回，冷启动的「鉴权重铸+重载一次」自愈腿不再保证被走到（页面真出 401
  时仍可判 Auth）。本批不为此加等待——加回等待就抵消本决定的收益；先按残留观察。

## Testing

- `DshShellForwardTests`：`Mint_ValidToken_ReleasesRouteWithoutProbing`、`Mint_DoesNotWaitForWebFace`
  （会话面 400ms 才就绪时铸币不等待、零探活）、`Mint_WrongToken_ReturnsFalseWithoutThrowing`；
  `Revalidate_*` 保留稳定窗/复合门/预算 fail-open/探针形状四类断言。
- `DshLoopbackProxyTests`：holder 长轮询在 303 即解除，探针形状断言改用收养重验触发。

## Related

- [holder-mint-gate-deepening](../feature/2026-10-01-holder-mint-gate-deepening.md)：门控位置与首屏完整度前提，本决定只撤冷启动的阻塞。
- [mint-epoch-mux-gate](2026-10-02-mint-epoch-mux-gate.md)：复合判据本身保留，用于收养链。
- [relay-restart-client-module-collapse](../bug-fix/2026-09-19-relay-restart-client-module-collapse.md)：收养稳定窗的来源。
- [cold-start-wait-collapse-and-proxy-cache-headers](2026-10-26-cold-start-wait-collapse-and-proxy-cache-headers.md)：同一条启动链的前一批，稳定化留作残留。
