# Agent Note: smoke-sw-render-probe（冒烟软件渲染探针）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

Related: 跟进 [`webkit-sandbox-userns-fallback`](../bug-fix/2026-09-26-webkit-sandbox-userns-fallback.md) 的重验证；dispatch 实证见本 ADR Testing。

## Problem

沙箱修复后重 dispatch（run `36229265859`）：core 消失、产品侧禁用行落证（runner sysctl 实为阳性——旧 ADR 注释"文件缺失"已证伪，同批订正）、P1/P2 再实证、①命中；但窗口在 dsh 就位前消失（仅 1 次占位到达，零授权零发起，Ryn 侧 `No window is available`，无崩溃无栈，进程存活致落定超时 FAIL）。伴随 `libEGL warning: DRI3 error`；Xvfb 无加速。渲染疑云未排除， deep-dive（全量日志/截图/Ryn 源码）成本高，先做一轮廉价对照实验。

## Decision

- 纯 CI 侧：冒烟步骤 env 加 `WEBKIT_DISABLE_COMPOSITING_MODE=1` + `LIBGL_ALWAYS_SOFTWARE=1`（关 WebKit 合成 + 强制 Mesa 软渲染）；产品零改动。
- 同批订正旧注释"runner 文件缺失"为"runner 同样命中阳性"（dispatch 日志禁用行为证）。
- 窗口存活即赚到第二跳证据；仍消失则转 deep-dive（对照组已就位，非白跑）。

## Alternatives considered

- **先 deep-dive 再动手**：落败（本次弃选）——全量日志/截图/Ryn 源码链成本高，一轮 15 分钟 CI 对照先行；失败则回此路，用户已拍板顺序。
- **产品侧默认软渲染**：落败——假设未证即改全用户渲染路径；CI 先证，有证据再议产品。
- **退回恒②（放弃全链）**：落败——Xvfb 投入 + fail loud 已两次抓真问题（bwrap core、本次窗口消失），回退等于丢探针。

## Consequences

- 代价：一轮 CI 约 15 分钟；无论成败都有判读价值（存活 = 渲染假说成立；消失 = 排除渲染，收窄到托盘/Ryn 生命周期）。
- 产品零风险（env 只活在 smoke 步骤）。

## Testing

- 真验证 = 本批 dispatch（提交时待跑）：看窗口是否存活到第二跳（`?token=` 到达 ≥2 次）或新的 fail loud 形态。
