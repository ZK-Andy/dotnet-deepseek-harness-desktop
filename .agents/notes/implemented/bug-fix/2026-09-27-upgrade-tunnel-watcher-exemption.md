# Agent Note: 升级隧道豁免页断联监视与收尾留痕

Status: implemented

Review: LIGHT/2026-09-27/R2=ok

Related: [`bug-fix/2026-09-27-upgrade-tunnel-host-authority`](2026-09-27-upgrade-tunnel-host-authority.md)——本篇去掉自家哨兵这一决定仍成立，但"隧道存活回归泵两端自然收敛"**未成为结果**：该轮上线后隧道依旧在同秒被 dsh 关闭、客户端持续退避重连（推断：没过 dsh 的 Host/Origin 门——缺 `Host` 即 403；该因果链未直接观测，标注见该篇 Problem），存活缺口由该篇独立修补。

R2 自审结论：改动收敛回环代理两文件 + 同目录测试；组合根/Core 未碰；升级分支与普通分支判据同源（`IsUpgrade` 与 `RelayAsync` 同头）；`linked` 仍承接应用退出取消；D003 无新增空 catch；0 Blocker。

## Problem

三平台冒烟截图（run `36318447847/36318449776/36318451895`，首个 Windows full-chain 当晚）左下角同挂 `Reconnecting...`：dsh 真 UI 已渲染、HTTP 全 200 且 cookie 有，`remote.mux` 隧道几秒内连建 5–6 条、关闭零行。根因在 `DshLoopbackProxy` 的 `WatchPageCloseAsync`：每连接（含升级）布哨，并发读页 socket 首字节，读到即判页已走并取消在途中继。升级后字节全是合法 WS 帧——哨兵偷走首字节（corrupt 帧流）又掐断泵，客户端循环重建。落定门数“建立次数”对此失明，照绿。

## Decision

- 升级连接不布哨（`IsUpgrade` 与中继分支同判据；`linked` 保留承接应用退出）。隧道存活回归泵两端 EOF/异常自然收敛。
- 隧道收尾 loud 一行（先关方=页/dsh/宿主取消 + 异常名 + 方法/目标/Upgrade 种），关闭不再零行。
- 未搬运官方登录：官方 `deepseek-account-login` 是 DeepSeek 平台 OAuth（模型凭据层）；我方 `WebAuth` 是回环 token 会话自愈（传输会话层）。徽章是传输重连，与登录无关。

## Alternatives considered

- **哨兵保留、只认 EOF 不认数据**：落败——升级连接上 EOF 前的数据全合法，哨兵读到数据就不能判走；留着它只剩误报价值，不如不布。
- **落定门改认隧道存活**：本次不做（用户明确只做 1、2 两项）——记为跟进：存活门数建立次数，对抖动失明。
- **隧道加计时器/心跳保活**：落败——ADR 原设计即“寿命与连接绑定，无计时器”；加计时器治的是另一个病。

## Consequences

WS 隧道不再被自家哨兵掐断；收尾先关方进 host.log，下次重连类问题直接定位到端。代价：应用退出时每条活隧道多一行“宿主取消”收尾（关机噪音，与既有 loud 风格一致）。

## Testing

- 新增 `Proxy_UpgradeTunnel_SurvivesClientFrames`（首帧回显排空 + 8K 完整性 + `宿主取消` 收尾断言）；旧码复演 114ms 即红（连接被掐致 EOF），新码绿。
- Infrastructure 全套件绿；`StubWebSocketServer` 加回显环（既有用例只做握手，不受影响）。
