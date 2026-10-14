# Agent Note: 免导航判据退场（恢复收口导航暂留）

Status: implemented

Review: FULL/2026-10-14/R1=ok R2=ok R3=ok

## Problem

窗口 URL 恒为回环代理根（[loopback-forward-proxy](2026-09-27-loopback-forward-proxy.md)）：路由不在 URL 里，落在哪由客户端自己的持久状态决定。恢复成功后壳导航回代理根重建整页——桌面形态下这次导航必然丢弃原路由，重建落点不受壳控制。

免导航判据（观测到页内导航提交即跳过壳导航）是这次导航的唯一护栏，而它自 2026-10-04 起不再命中：`~/.dsh/logs/host.log` 最后一次「跳过代理侧导航」为 2026-10-02 00:13:25，10-04 至 10-14 每日 1–4 次收养、命中 0 次【探索性：单机日志计数，未做跨机对照】。窗口内市场页 `awaitNewBoot()`（轮询市场状态端点，boot 变化即 `location.reload()`）拿不到可用应答是成因之一：10-04 至 10-06 每日 26–32 条「代理未铸币即调用」502，2026-10-14 19:43 那次的 6 条市场 502 来自市场插件自身【推断 · 未证：市场 helper 就绪时点与壳侧铸币门控各自贡献多少，未做对照】。落地症状（2026-10-14 19:43 装插件触发的市场重启，n=1【探索性】）：窗口被导航回裸根后落到一张未访问过的市场子页并渲染错形。

同一批证据反向钉住了另一点：命中口径正是「恢复周期内有页面导航到达」，零命中即这些周期里**页内自刷从未发生**；而承载失败面的脚本（`RecoveryPageBuilder`）覆写整个活动文档、自身不含 `reload`，页面健康探针只在页面空白（`Dead`）时才 reload，覆写后的失败面有可见文本即 `Alive`——**被删掉的那次壳导航恰是恢复后唯一的实际回程**。故本决定只退场判据，不动导航。

上游把恢复放在页面内与原生层，壳不重导航（2026-10-14 读）：官方 web 客户端 `ConnectionController` 原地重连（Ping/Pong 踢链 + 退避封顶 + 三态连接指示）；官方 desktop 让后端生命周期独立于窗口、只发布 `starting/ready/error`，`fatal-recovery` 用原生对话框承载致命失败，`will-navigate` 只在 URL 不同时放行；两家第三方壳同向（只在 origin 不同时加载 harness URL、客户端重启面不发页面导航）。这些是本篇 Alternatives 里「零导航只在承载面不碰 DOM 时成立」的外部依据。

## Decision

1. **免导航判据退场**：删 `AdoptNavigateGate`（纯函数）与其观测信号 `RynNavigationCallbacks.LastNavigatedAtUtc`（含 `_navStampGate` 锁）——判据零命中即死重，观测信号无其他消费者。
2. **恢复回调改名、收口导航保留**：`RuntimeSupervisor` 的 `navigate` 回调改名 `onRecovered`（语义＝「重启已给出新 URL」，不再是「去导航」）；实现先重验铸币态（epoch 可能已换：新 secret/端口；token-free 路径），再补一次回代理根的导航。承载面仍覆写活动页 DOM，故这次导航是唯一回程，**不在本决定内删除**。
3. **零壳导航的成立条件**（交给承载面切换篇）：只有当失败面不再触碰活动页 DOM 时，壳才能省掉这次导航；届时的判据应是「承载面是否覆写 DOM」，而不是「页内是否自刷」——后者零命中已证不可依赖。
4. **窗口导航面**：首载、origin 变化、页面健康 reload、引导期鉴权重载，外加本次恢复收口。

## Alternatives considered

- **无条件零壳导航（落败）**：承载面覆写活动页 DOM 时页面与其 SPA 状态已毁——失败面脚本无 `reload`、健康探针见可见文本即 `Alive`、判据零命中即页内自刷从未发生；删导航会把「导航回裸根丢路由」换成「恢复后停在只剩退出的失败面」（本批复审实测，n=1 形态【探索性】）。零导航只在承载面不碰 DOM 时成立，随承载面切换落档。
- **保留壳导航 + 宽限窗（先等页面自刷，窗满再导航）（落败）**：给一次不该发生的导航加计时器，复杂度上升且落点仍由壳决定；页面未自刷时症状与现状一致。
- **只换免导航判据（由「观测到页内自刷」改为「本次收养来自市场接力」）（落败）**：仍保留壳导航，并把市场耦合写进壳判据；市场侧就绪慢时同样丢路由。
- **恢复收口改由失败面自带 reload（落败）**：脚本覆写后页面是静态失败面，「何时恢复已好」这一信息在页面侧不可得（壳已持有），固定节拍 reload 会在多次重启失败时与监督器重试节拍叠成闪烁环。
- **保留到达时间戳观测以备将来（落败）**：无消费者的观测信号只会在下一次改动里被误当依据；需要时回 git 历史取篇。

## Consequences

- 买到的：判据、观测信号与其锁一并退场（净减代码）；恢复回调的语义不再谎称「导航」（改名 `onRecovered`）；收口导航的必要性在代码里写明（承载面覆写 DOM）。
- 付出的：承载面切换前恢复路径仍有一次导航，原路由仍会丢——这是承载面问题的表症，本决定不解决。
- 取代关系：[adopt-skip-navigate-on-self-reload](../bug-fix/2026-09-25-adopt-skip-navigate-on-self-reload.md) 的免导航机制整体删除（该篇已记 Erratum）。
- 与下一篇的关系：承载面切到不碰 DOM 的原生框后，第 2 条的收口导航据第 3 条退场（Agent Note `2026-10-14-native-failure-surface`，随承载面切换落档）。

## Testing

- `dotnet test` 全绿 0 警告：1037（基线 1042 → 1037，`scripts/test-baseline.json` 与 README 双语徽章同步）。
- 删除：`AdoptNavigateGateTests`（4 例）与 `RynNavigationCallbacksTests.Navigated_RecordsArrivalTimestamp`（信号已无消费者）。
- 更新：`RuntimeSupervisorTests` 记序改钉「失败面先于恢复回调」与「重启给出 URL 才回调」；`CompositionRootSequenceTests.StartupSequence_RecoveryStart_InvalidatesMintRoute` 锚改为方法声明行 `private ValueTask ShowFailureSurfaceAsync(`（签名形参变化不假红；mutation：删 `InvalidateRoute()` 调用即红）。
- 覆盖率：本地按 CI 口径复算（`dotnet test -c Debug --collect:"XPlat Code Coverage"` + `scripts/coverage-summary.py` 并集）63.88%（基线 63.59 → 63.88）。
- 门禁：compose-root 16/16、registration 11/11、code-conventions OK、code-health OK、ui-copy OK、increment-discipline 见提交面。
- 实机未验：恢复窗口内「覆写失败面 → 重启 → 收口导航」这一链需真实桌面；沙箱只验证启动。

## Related

- 上游对照依据（外部仓，2026-10-14 读取）：deepseek-ai/deepseek-harness 的 `packages/client/connection/src/client/connection.ts`、`apps/desktop/src/backend-controller.ts`、`apps/desktop/src/fatal-recovery.ts`、`apps/desktop/src/main.ts`；dataelement/dsh-desktop 的 `src/main/window-navigation.ts`；dsh-tauri/deepseek-harness-desktop 的 `packages/dsh-tauri-extension/src/client/service/restart.ts`。
- 已装 dsh 侧对照：`dsh-client-hmr/lib/client.js` 只有模块级 `entries.sync/reload`，整页 reload 仅存在于市场插件 `dshmarket/client/client.js` 的 `awaitNewBoot()`。
