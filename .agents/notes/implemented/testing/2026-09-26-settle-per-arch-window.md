# Agent Note: settle-per-arch-window（落定窗分架构）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

Related: 证据为 dispatch run `36237256739`（arm64 超时行缺席 ⟹ hop1 发起距 kill 不足 30s ⟹ 窗就绪在启动后约 103s+）。

## Problem

arm64 原生建窗 100s+（dbus 未治愈；托盘错误变形证明总线已存在，建窗仍慢——根因仍在 GTK/WebKit 初始化深处，Ryn#101 跟进中），而落定窗全腿 90s：hop1 在 ①后约 70–100s 才发起，90s 窗内等不到提交即收工——等的一方先超时，不是不通。amd64 建窗快（hop1 在 ①后秒级），90s 绰绰有余。全腿统一加长会拖慢 amd64/失败路径的反馈；只给慢腿加长是精准匹配。

## Decision

- 矩阵加 `settle_seconds`：amd64 90（不变）/arm64 300，经既有 `SMOKE_SETTLE_SECONDS` 口透传（脚本 `SETTLE_WAIT` 默认 90 不动，rpm 容器腿自动继承——容器恒②，无窗可落定，值变无影响）。
- 零产品改动，零脚本改动（口子早已存在）；APP_TIMEOUT（720+20=740）覆盖 300s 窗是有条件的：可用落定预算实为 740−t①（①时刻），①晚到超约 440s 则应用超时先截断（此时报"落定期进程退出"而非"落定超时"，可区分；预期①@~103s 可跑满）。
- 仍红即收敛：arm64 若 300s 窗仍无提交，排除"窗短"假说，转回原生挂起 deep-dive（Ryn#101 加证据）。

## Alternatives considered

- **全腿加长到 300s**：落败——amd64 与失败路径的反馈慢 3 倍，无收益；分腿精准。
- **加长窗口等待（120s→更大）而非落定窗**：落败——瓶颈不在等窗（hop1 已发出），在等提交；加错窗。
- **接受 arm64 红不再追**：落败（本次弃选）——一轮 5 分钟 dispatch 即可证伪"窗短"假说，成本低；失败则回此路。

## Consequences

- arm64 失败路径 CI 最坏 +3.5 分钟；通过路径提前退出，无代价。
- amd64 零变化。

## Testing

- 真验证 = dispatch：arm64 看 full-chain（截图人眼复核）或新形态 fail loud。
