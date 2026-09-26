# Agent Note: bootstrap-window-ready-wait（引导等窗口可用）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

Related: 根因由 [`smoke-sw-render-probe`](../testing/2026-09-26-smoke-sw-render-probe.md) 的 deep-dive 定位（Ryn 源码对读 + dispatch 全量日志时序）；承接 [`webkit-sandbox-userns-fallback`](../bug-fix/2026-09-26-webkit-sandbox-userns-fallback.md)（沙箱修好后暴露本问题）。

## Problem

dispatch `36229265859`/`36230153044` 两连证（排除渲染后定稿）：loop 从未退出（无 `Ryn Run 结束/异常`）→ 窗口非被关；Ryn `MainWindow` 为 null 只剩"从未注册"；回调失败（①后瞬间）早于占位到达与健康 alive（文件序）→ 原生建窗晚于 dsh 就位。CI 无 D-Bus 会话（托盘 `address` null 实证、a11y bus 报错）拖慢 GTK/WebKit 原生初始化 30s+，而 bootstrap 在 `onRuntimeReady` 首个 `Current` 即抛后失败收口、永不重试——窗建好时已无人导航。慢建窗在真机同样可达（首次 fontconfig 构建等），属产品脆弱性，非纯 CI 问题。

## Decision

- 产品：`EnterMainUiAsync` 入口先有界等窗口可用（新 `WaitForWindowAsync`，对标 `ProbeVisibleTextAsync` 取消语义：应用退出 OCE 上抛，超时回 false）：就绪即进，超时 loud 日志跳过本次导航、不抛。只捕 `InvalidOperationException`（Ryn 两处"无窗口/未运行"同此型），其余异常照抛。
- 新超时进配置模型 `WindowReadyTimeoutSeconds`（默认 120s，从回调起算：CI 实证回调时窗未建、建好不晚于启动后约 122s（回调约在启动后 30s），120s 窗覆盖该区间并留余量；超时仍 loud 保底）+ 轮询节拍 `WindowReadyPollIntervalSeconds`（默认 1s，同类节拍键先例）。
- CI：冒烟步骤包 `dbus-run-session --`（还原文会话总线，加速原生初始化；托盘失败同愈可期）+ apt 加 `dbus`（幂等）。与产品等待双轨互补：CI 还原快路径，产品兜慢机器。
- 伴随信号备忘（非阻塞，不动手）：`dsh 版本探测失败 No such file`（横幅任务早于 prefix 暴露的探针竞态，日志行而已）；截图 274B 空包（FAIL 跑 kill 后无窗可拍，best-effort 本色）。

## Alternatives considered

- **只加长落定等待（smoke 侧）**：落败——病不在 verdict 时机，bootstrap 在①后瞬间已失败收口，等再久也无人导航。
- **无界等到窗口可用**：落败——建窗根本失败时（从未成功路径）bootstrap 永不 settled，拖住退出/更新链；有界 + loud 才 fail-safe。
- **只做 CI dbus（不动产品）**：落败——慢建窗真机同样可达，CI 变快只掩盖产品脆弱；用户拍板 A+B。
- **回调内吞掉异常稍后重试导航**：落败——与有界等待同代价但语义散（重试散在后台任务）；入口一次等足，失败面单一。

## Consequences

- 快机器零变化（首探即中）；慢建窗首启不再死（等到即进，超时 loud）。
- 代价：极慢机器（>120s 建窗）仍跳过本次导航——显式日志 + 重启即进（dsh 已就绪，二次回调落在等待窗内）。
- dbus 会话可能顺带修好托盘初始化（待 dispatch 日志验证，非承诺）。

## Testing

- 配置矩阵跟值（超时默认 120/节拍默认 1 + 全键覆盖 26/27）；接线薄层沿既有先例不单测。
- 真验证 = dispatch package-linux：预期第二跳到达（`?token=` ≥2 次）+ full-chain；托盘行是否转好顺带看。
