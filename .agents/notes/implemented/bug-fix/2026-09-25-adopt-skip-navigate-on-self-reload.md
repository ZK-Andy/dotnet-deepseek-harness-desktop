# Agent Note: adopt-skip-navigate-on-self-reload（市场重启页内自刷与壳收养导航叠加成两跳）

Status: implemented

Review: FULL/2026-09-25/R1=ok R2=ok R3=ok

## Problem

插件市场内安装插件后点一键重启，页面可见**跳两次**：市场页先刷出新内容，随即又被拽回根再刷一次。`adopt-first`（2026-09-15）与塌陷修复（2026-09-19 A/B）均未消除。

**实证（2026-09-25 20:00 真机，`~/.dsh/logs/host.log`，dsh 0.1.7-rc.2 / 壳 v0.5.5）**：

- `20:00:52 [supervisor] dsh 子进程退出，执行恢复…`
- `20:00:57 [host] 市场接力续任者端口 35263 已监听但 web 面未就绪：继续等待，不导航进空白页`
- `20:00:57 [nav] 导航已到达：http://127.0.0.1:35263/`——壳此时仍在 `TryRideMarketRelayAsync` 等待中，未发起任何导航；健康探针亦无 `触发有界恢复 reload` 留痕。故这次到达只能是**页内发起**。
- `20:01:00 [host] 市场接力续任者已接管首选端口 35263（就绪连续维持 ≥2s）…转入收养处置` → `[supervisor] 重启成功 → http://127.0.0.1:35263/` → 第二次 `[nav] 导航已到达`——这次才是壳的收养导航。

**页内发起方（市场 `doRestart`，`dshmarket/client/client.js` 约 8222～8266 行）**：`POST /dsh-market/restart` 后以 1.5s 节拍轮询 `GET /dsh-market/status`，`next.boot !== previousBoot` 即 `location.reload()`，**无稳定窗**。它只看 status 的 boot id，比壳的 web 面就绪 + 2s 稳定窗判据早约 3s 命中——与 host.log 的两跳间隔一致。

**前两批未覆盖的原因**：`adopt-first` 消的是壳自己的竞争 spawn 带来的第二次导航，`Alternatives considered` 从未列出"市场页自己会 reload"；塌陷修复的 B（稳定窗 2s）把壳导航又推迟 ~2s，反而把两跳从"几乎同时"拉成肉眼可辨。另有加重项：页内 `reload` 保留当前路由（停在市场页），壳收养导航是裸 `/`（`AdoptSuccessor`），会把路由拽回根。

## Decision

收养时**页内已自刷即免导航**：壳仍做收养登记（pid/token 落盘、IPC origin 授权、`_webUrl` 刷新），但跳过 `NavigateAsync`。

接线（三处，皆小）：

**1｜到达时间戳（Presentation，`RynNavigationCallbacks`）**：`OnWebViewNavigated` 内同步刷新 `LastNavigatedAtUtc = DateTimeOffset.UtcNow`（`lock` 独立小锁，与既有 `_onNavigatedImpl` 的 `Volatile` 各管各的类型：可空结构体要多字原子，引用发布用 `Volatile`）。恢复页的 `EvaluateJavaScriptAsync(BuildScript)` 只覆写 `innerHTML`，不产生导航提交，不污染该戳。

**2｜免导航纯谓词（Core，`AdoptNavigateGate.ShouldSkipAdoptNavigate`）**：`lastNavigatedAtUtc >= relayStartUtc` 即跳过；`null`（从未到达）不跳过。放 Core 而非组合根——R1 组合根只装配，裁决语义须可单测。

**3｜周期起点（组合根，`SetupSupervisor`）**：`showRecovery` 回调内记 `_lastRecoveryShownAtUtc = UtcNow`（恢复周期起点：子进程退出后、`RestartAsync` 等待前）；`navigate` 回调内以该起点问谓词，命中（周期内有到达，视为页内自刷——谓词只比时间戳，不验 origin，同源靠下段前提假设支撑）则只更新 `_webUrl` + 授权 origin + 留痕，不调 `NavigateAsync`。

该信号成立的前提：恢复周期内到达 ≈ 页内自刷——恢复页无可导航链接，外部导航被 `Block` 不提交，宿主程序化导航在等待期内不存在。非市场页（停在会话页时后端被重启）无自刷，谓词为假，走既有导航兜底塌陷；真塌了健康探针的有界 reload 仍在。

## Alternatives considered

- **壳导航保留当前路由而非裸 `/`**：落败——同 URL 再导航仍是一次可见闪动，且壳侧取 WebView 当前 URL 不可靠；治标不治本。
- **市场侧去掉自 reload、全交壳导航**：落败——依赖上游节奏；且非桌面浏览器用户依赖该自刷，无桌面时回退路径丢失。
- **壳稳定窗对齐页内 1.5s 轮询、抢在页内之前导航**：落败——竞速不可靠（status 与 web 探针的就绪时点本就不同源），抢赢一次不保证次次赢；赢了也只是把"页内刷"换成"壳刷"，仍可能叠加。
- **按 boot id 比对决定免导航（壳记旧 boot、收养后读新 boot）**：落败——需新增对 `/dsh-market/status` 的壳侧依赖（R3 新边界），而到达时间戳是 Ryn 回调已有信号的零依赖复用。
- **一律免导航（收养永不导航）**：落败——非市场页无自刷能力，塌陷兜底丢失；免导航必须以"观测到自刷"为条件。

## Consequences

- 市场页点重启：单次视觉刷新（页内自刷），壳只留痕不导航；`_webUrl` 仍指向收养 origin，健康探针 reload 靶点不受影响。
- 非市场页场景：行为零变更（谓词为假，走既有导航）。
- 误判上限：若自刷命中的是半就绪页（status 先于 web 面），最坏与现状一致（用户看到一次页内刷 + 健康探针兜底），不比现状多一次导航。
- 观测面：新增 `收养后检测到页内已自刷…跳过壳侧导航` 行，与既有 `重启成功 →` 行对偶，可供 host.log 判读。
- 上游若去掉市场自 reload，谓词恒假，自动退化为既有行为，无害保留。

## Testing

- Core（`AdoptNavigateGateTests`）：`null` → 不跳过；早于起点 → 不跳过；等于起点 → 跳过；晚于起点 → 跳过。
- Presentation（`RynNavigationCallbacksTests`）：初值 `null`；一次 `OnWebViewNavigated` 后戳非空且 `>=` 到达前读取的起点。
- 既有 `HarnessRuntimeHostTests` 接力闭环、健康探针用例不动（收养 URL 语义不变，只是导航与否由组合根裁决）。
- 实机验收：市场内安装插件后一键重启，host.log 应见 `重启成功 →` 紧跟 `跳过壳侧导航`，`[nav] 导航已到达` 在周期内只出现一次（页内那次）。

## Related

- [market-restart-adopt-first](../feature/2026-09-15-market-restart-adopt-first.md)：本决定补其未列的"市场页自 reload"分支；收养链复用其 `RecoverFromFailureAsync`。
- [relay-restart-client-module-collapse](2026-09-19-relay-restart-client-module-collapse.md)：其稳定窗把两跳拉开为可见；本决定消除叠加而非动稳定窗。
- [relay-web-readiness](2026-09-16-relay-web-readiness.md)：web 面就绪判据不动；页内 status 轮询与壳 web 探针不同源是两跳时序差的来源。
