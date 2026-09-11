# Agent Note: drift-command-channel-selfheal

Status: proposed

## Problem

端口漂移后「页面 → 壳」命令通道在**本会话**失效：Ryn 的 IPC CORS 允许源在窗口创建时钉死（`RynOptions.Url` 的 authority），因此**窗口已按 dsh URL 建好之后**页面 origin 再变（监督器崩溃重启换端口）时与之不符，预检拿不到 `Access-Control-Allow-Origin`，浏览器拦下所有 `window.__ryn.invoke`（根因与实测见 [port-drift-ipc-origin-mismatch](../../implemented/bug-fix/2026-09-12-port-drift-ipc-origin-mismatch.md)）。

当前处置是**告知**：host.log 记后果、设置页提示「重启应用可恢复」。用户仍需自己发现并重启——漂移是壳自己造成的，代价却由用户承担。本笔记评估「壳自愈」这条路，供拍板。

## Proposal

候选（可组合，非互斥）：

- **A · 漂移即重启壳**：漂移落地（`RecoverFromFailureAsync` 回退分配成功）后，壳以自身 `Environment.ProcessPath` 重新拉起自己并有序退出。新进程用本次实际端口作为 `RynOptions.Url`，页面 origin 与 IPC 允许源天然一致，命令面即刻恢复，用户无需知情（代价与准入准出见 Consequences）。
- **B · 漂移后重建 WebView/窗口**：若 Ryn 将来把 IPC 允许源做成可更新（或暴露「带新 origin 的窗口重建」接口），可只重建窗口、不动进程。当前不可行：`LocalWebServer` 是 `RynWindow` 内部构造、允许源无更新入口（上游 PR 正把 CORS 判据放宽，见 Related）。
- **C · 维持仅告知**（现状）：零新风险，但每次漂移都要用户手动重启，且只在用户看得懂 host.log 时才发现。

## Alternatives considered

- **让页面自己重连（前端轮询新 origin）**：落败——页面不知道壳的新端口，也没有跨 origin 读端口文件的通道；这是壳的职责。
- **把 dsh 挂在固定端口上（壳侧代理）**：落败——为一个偶发故障引入常驻代理与新的失败面（代理本身崩溃/端口再冲突），成本与收益不成比例。
- **把漂移本身消灭（启动前必清占用者）**：已在做的方向（[运行时交接收养](../../implemented/bug-fix/2026-09-12-runtime-handoff-adoption.md) 的血统收割使漂移变少），但「占用者非我方血统」时无法清，漂移仍有残留面，故仍需本笔记的兜底。

## Consequences

- 候选 A 的代价与必配约束：① 端口持续被占时可能重启循环——需次数上限或退避（环境变量携带尝试计数，超限退回「仅告知」并记 host.log）；② 重启丢窗口态与页面未提交状态，`hide-to-tray` 用户可能正看着页面；③ 装机形态可自拉（`/usr/lib/deepseek-harness-desktop/`），dev 运行时同样可自拉，但与单实例锁 / 隔离 home 的交互需分别验证。
- 准入准出：A 需①循环有界（上限 + 退避 + 落日志）、②重启前把「即将重启」推给页面（至少在设置页可见）、③冷启动路径与 dev 路径各一次实机复验。B 需上游先给出接口；C 是现状基线。
- 若上游 [issue #90](https://github.com/Yupmoh/Ryn/issues/90) / [PR #91](https://github.com/Yupmoh/Ryn/pull/91) 合并并被本仓 bump，A/B 的收益面基本消失（漂移后命令面自然可用）——届时本篇按 `rejected — <理由>` 收口或连档删除。
- 未决：不在本批实现；拍板后按 feature-flow 走实现与评审。

## Related

- [port-drift-ipc-origin-mismatch](../../implemented/bug-fix/2026-09-12-port-drift-ipc-origin-mismatch.md)：根因、实测证据与已落地的告知面（本篇的前置）。
- [子进程收割与端口漂移](../../implemented/bug-fix/2026-08-26-child-process-reaping-port-drift.md)：漂移机制与告警的出处。
- 上游 [issue #90](https://github.com/Yupmoh/Ryn/issues/90) / [PR #91](https://github.com/Yupmoh/Ryn/pull/91)：`BuildCorsHeaders` 与 `IsAuthorized` 判据对齐；合并并被本仓 bump 后，A/B 都不再必要（漂移后命令面自然可用）。
