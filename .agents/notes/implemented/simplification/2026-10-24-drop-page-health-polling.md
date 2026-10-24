# Agent Note: 删除页面健康轮询，空白页交还上游

Status: implemented

Review: FULL/2026-10-24/R1=ok R2=ok R3=ok

增量闸豁免：gross ≤ 1000，fanout ≤ 15，packages ≤ 1（纯删除批次：暂存集 835 行、origin/main..HEAD 累积 924 行含两枚未推 docs 提交；拆分会留下不可编译的中间提交）。

## Problem

壳每 10 秒对页面跑一次 `document.body.innerText` 探针，连续 3 次空文本触发有界 reload（最多 3 次），针对的是 dsh 客户端模块热替换后的塌陷白屏。三项已证代价：

1. `innerText` 每次强制整文档 layout，重页面上会打断渲染主线程；
2. 主线程被占死时求值排队不返回——它在最需要的时候失明（内存失控那次未留下任何 dead 记录）；
3. 它只能对 DOM 形态做猜测，没有任何平台信号可依据。

该故障的成因在 dsh 客户端（模块塌陷），不在壳。壳侧轮询属于替上游缺陷兜底。

## Decision

删除 `PageHealthMonitor`、`PageHealthTracker`、`PageHealthRecovery`、`RuntimeTimeouts.HealthInitialDelaySeconds`、诊断包 `state.txt` 的 `health` 行及其配置。页面塌陷为空白后不再自动恢复，用户手动重启应用。

被取代的机制见 [page-health-monitor](../process/2026-08-26-page-health-monitor.md)。

平台层本来提供正确的信号（WebView2 `ProcessFailed`、WKWebView `WebViewWebContentProcessDidTerminate`、WebKitGTK `web-process-terminated`），但 Avalonia 的 `NativeWebView` / `IWebViewAdapter` 未把它们暴露给应用。已向上游提 issue：AvaloniaUI/Avalonia.Controls.WebView#102（含事件形状建议与三适配器映射）。在其落地前不做壳侧替代实现。

## Alternatives considered

- **保留但降频或只在导航后探一次**：仍是为上游缺陷兜底；降频只减少开销，不改变归属。
- **改心跳（页面写 `data-*`，壳只读）**：要求改第三方 dsh 客户端，且页内脚本同样在主线程卡死时失效。
- **壳侧自建平台进程观测（Win32/WKWebView/WebKitGTK 各一套）**：等于自己维护一份 WebView 适配器，成本与风险都高于等待上游补事件。

## Consequences

- 空白页不再自动 reload，只能手动重启；这是明确接受的残留代价。
- 诊断包不再有 `health` 行，排障以 host.log 与截图为准。
- 删除 287 行实现与 4 个测试类、35 个用例（本地实测 1082 通过；基线数由 `scripts/test-baseline.json` 承载，本批不复制、下一批跟值），`RuntimeTimeouts` 少一个键。
- 上游事件落地后若需要恢复能力，应接事件，不得重建 DOM 轮询。
