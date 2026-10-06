# Agent Note: 未铸币转发请求改有界等待铸币

Status: implemented

## Problem

回环代理对未铸币的转发请求（普通请求与升级隧道）一律立即 502。市场插件更新的收尾会调度 dsh 重启（`.dsh-market/log.ndjson` 的 `update → restart scheduled` 模式），壳经「接力收养」恢复；2026-10-07 实机事件（codegraph 0.6.5 更新）实测该窗口 12 秒，期间旧页面仍在运行，其 XHR 与 SSE 重连共吃到 19 条未铸币 502——市场组件按设置逐个调家、全部闪失败态，用户感知为「过渡页反复刷新才恢复」。恢复本身按设计完成，毛刺全部来自「立即 502」与「等待铸币」的不对称：`/__shell_ready` 探针在未铸币时长轮询等铸币，普通请求却零等待即拒。

## Decision

未铸币的转发请求不再立即 502，改为有界等待铸币门放行（`DshShellForward.WaitMintedBoundedAsync`，等待位与 holder 长轮询同一 epoch 化未决门）：

- 预算 `DefaultUnmintedWaitBudget` = 15s（覆盖实测 12s 收养窗口 + 余量；非用户可调行为，不进 RuntimeTimeouts，与铸币稳定化参数同口径）。预算到点仍无铸币才回落既有 loud 502，日志行带等待时长。
- 等待期取消（页断联/应用退出）上抛 `OperationCanceledException`，由调用方静默收尾——不新增噪声行。
- 等待返回后调用方仍须 `TryGetRoute` 复核：等待者持的门可能恰逢 `InvalidateRoute` 换门（epoch 翻转），route 才是权威。
- 生效面：`DshLoopbackProxy.RelayAsync`（普通转发）与 `DshLoopbackTunnel.RelayUpgradeAsync`（升级隧道）两条路径同口径；`/` 与 `/index.html` 的 holder 面、`/__shell_ready` 探针、`/__shell_guide` 指南页不经过等待（本地端点原语义不变）。
- 时间经 `DshShellForward` 既有 `TimeProvider` 缝；代理侧预算经内部构造参数注入（测试压缩时长）。

## Alternatives considered

- **保持立即 502，由 SPA 自行重试**：现状即此——EventSource/轮询确实会重试成功，但每次重试都要吃一轮 502，市场组件在窗口内持续闪失败态，体验毛刺即用户报告的问题。落败。
- **未铸币等待不设预算（与 holder 同为无界）**：dsh 真死且无续任者时（监督器仍在重试 spawn），请求将无限挂起，页面从「快速失败」退化为「冻结」。落败——有界是本决定的底线。
- **502 响应带 Retry-After**：不解决窗口内组件闪失败（SPA 未必遵从），也不消除 19 条 502 的日志噪声。落败。

## Consequences

- 买到：插件更新收养窗口内旧页面无感恢复（XHR/SSE 重连被等待吸收，放行后照常转发）；窗口内零 502 噪声。
- 代价：dsh 真死且恢复无望时，每个未铸币请求从 0s 变为最长 15s 才 502（诊断面上首次失败变慢；后续重试同预算）。升级隧道无页断联哨（升级连接豁免监视），页已死时等待占满预算——有界、可接受。
- 预算参数与稳定化参数同为代码内常量；调整走变更 + 测试。

## Testing

- `DshLoopbackProxyTests`：未铸币 502 用例改为注入 50ms 预算（预算到点路径）；新增普通请求与升级隧道两条「等待吸收」用例（请求先入场、铸币 mid-flight、断言成功 + 未铸币行零落盘）；新增升级隧道预算到点 502 用例。
- 铸币等待原语单测在 `DshShellForwardTests` 既有 `WaitMintedAsync`/`InvalidateRoute` 面之外，经代理级用例覆盖（等待位与门共实现，不重复直测）。

## Related

- [loopback-forward-proxy](../architecture/2026-09-27-loopback-forward-proxy.md)（代理本体；未铸币语义由本篇更新）
- [mint-epoch-mux-gate](../bug-fix/2026-10-02-mint-epoch-mux-gate.md)（epoch 化门控与收养重验）
- [proxy-log-noise-reduction](../architecture/2026-10-06-proxy-log-noise-reduction.md)（日志档位；等待面未新增逐请求行）
