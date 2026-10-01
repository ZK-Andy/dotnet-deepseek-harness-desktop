# Agent Note: companion-switch-degradation-recovery

Status: implemented

Review: FULL/2026-10-01#3/R1=ok R2=ok R3=ok

## Problem

2026-10-01 实机（0.5.14，pid 66818，Fedora 44）：设置 → 桌面设置页显示「异常」，两个开关（开机自启、关闭时最小化到托盘）点不动；重开设置页即恢复，随后反复出现。

同一页面、同一分钟的取证：

- **通道健康**：16:23:41–16:23:53 连点 5 次「导出」，宿主每次落 `[host] 诊断包已导出：…（5 项）`；壳对页面的探针 16:23:44 记 `health: alive (probes 545)`；`127.0.0.1:7421` 对坏 token 秒回 403，页面 origin `http://localhost:33625` 的预检带 `Access-Control-Allow-Origin`。
- **壳侧无异常**：三个路由（autostart / closeToTray / diagnostics）在 `RegisterServices` 无条件注册；无端口漂移、无 EADDRINUSE；`[host] 系统托盘已注册` 早于窗口出现，故「无系统托盘」必为误判。
- **落盘面零动作**：`~/.dsh/desktop-preferences.json` mtime 停在 08-25、`~/.config/autostart/deepseek-harness-desktop.desktop` 停在 09-20——当天的开关点击一次都没到壳的 set 路径。

代码面成因（确定）：三个区块的初值都来自 `window.__ryn.invoke`，但只有更新块有兜底（`invokeWithTimeout` 4s + 通道探针），两个开关行是裸 invoke——

1. `CloseToTrayRow` 把任何失败/无答案渲染成 `available:false`，即**冒充「当前运行环境无系统托盘」**并禁用开关；
2. `DesktopSection` 的 `then` 里读 `p2.enabled` 没有 else 分支，非合法帧（`parseFrame` 只返 null、不抛）静默不置状态，状态永远停在 `null`（开关 disabled）；
3. 两行都没有超时与重试，挂载那一刻的失败对用户是**永久态**（只有重新 mount 才重发）。

此类失败只活在页面内存里，host.log 上零证据，复盘只能靠现场探测。

【推断 · 未证】挂载时刻那次 invoke 丢失的具体触发（桥接就绪时序 / 主线程忙碌 / 单次请求丢失）：通道级证据只证明「同一分钟其它命令通」，不足以定位单次丢失的起点。本批不押注触发因。

## Decision

1. **开关状态三态化**：`null`（装载中）/ 合法帧（`{enabled,…}`）/ `{channelDown:true}`（通道失败）。只有合法帧才可交互、才下结论（装载中渲染禁用态开关）；通道失败渲染「状态未知 + 重试」按钮（新文案 `hostUnreachableDesc` / `retry`，zh/en 双字典同键集）。「宿主明确答 `available:false`」与「没答案」从此处置分离。
2. **装载统一兜底**：`loadSwitchState` 首拉 + 800ms 重试一次，两次都失败才判 `channelDown` 并上报；`useSwitchState` 返回 `[state,setState,retry]`，`retry` 递增 nonce 重挂载整段装载——失败可自愈，不必重开设置页。
3. **更新块不再锁死**：通道失败在首次挂载后自动重查一次（3s），提示态给「重试」入口（重挂载整段 effect）。
4. **失败留痕**：新增宿主路由 `desktop.companion.report`（`CompanionReportCommandRouter` → `HostLog`；只写日志，坏载荷静默，行分隔符全折叠 + 截断）。页面侧失败先积压（同 scope+message 去重）并**入队即首投**（裸 invoke 先发一次）；首投失败或悬挂即回队，再由本页下一次成功调用（开关重试 / 更新重试 / 探针，即经 `invokeWithTimeout` 的命令）补投——通道本身失败时用同通道报障必然一起失败，留痕只可能发生在「通道回来之后」；补报自身也接 8s 结算兜底（桥接 invoke 可能既不成功也不失败）。队列是页面内存态。
5. **导出文件名到毫秒**（`yyyyMMdd-HHmmssfff`）：旧形态同秒连点撞名，把「文件已存在」误报成「文档目录不可写」并把包回退到 `~/.dsh/diagnostics`（实机 16:23:52）。
6. **伴生插件 0.0.19 → 0.0.20**：装机侧随包升级判定按版本走，不 bump 拿不到修复。

## Alternatives considered

- **只加超时/重试，不动文案与三态**：落败——失败仍被渲染成「无系统托盘 / 已关闭」这类具体结论，用户按错误信息排查（本次实机就是这样绕了两个小时）。
- **复用既有 `desktop.*` 路由承载报障**：落败——既有路由都带状态语义，报障是纯诊断面，复用会污染其帧契约。
- **报障即时发送、不等通道恢复**：落败——通道失败时同通道发送必然一起失败（本次 5 次导出之外的失败无痕正是如此），等于没有。
- **壳侧给全部 IPC 命令加收发留痕**：落败（本批不做）——收不到的请求壳侧无从记录；能记录的是「已到达的命令」，与本缺陷形态（请求压根没结算）不重合，成本却覆盖全部路由。
- **为客户端引入 JS 测试框架（jsdom + React 假件）钉三态**：落败（本批不做）——仓库无 JS 测试基建，新增测试运行时属独立立项；本批以 `node --check` + UI 文案门禁 + 宿主侧 xunit 兜底，客户端三态留实机验证。

## Consequences

- 开关不再假报「无系统托盘 / 已关闭」：通道失败显示「状态未知 + 重试」，可点、可自愈。
- 首次失败即留痕：通道恢复后页面补报 `[companion] 页面降级上报：…`，下次同类问题不必再靠现场探测复现。
- 更新块在通道失败的当次会话仍有「3s 自动 + 手动重试」两条收敛路径。
- 残留：客户端三态无自动化测试（见 Alternatives）；触发因仍未定位（Problem 的【推断】），本批消除的是「一次失败放大成永久误导」这一层。
- 残留：补报队列是页面内存态，页面卸载即丢；flush 触发面是经 `invokeWithTimeout` 的成功调用（raw invoke 的成功不触发补报）。要跨卸载可靠留痕需页面侧持久化，另立项（见 Alternatives 的即时发送条与 Decision 4）。
- 装载路径的宿主错误帧（`{"error":…}`）不再被并成「unexpected frame」：原因进上报与 host.log，而不是只留下「状态未知」。两条 set 路径仍记通用 `unexpected frame; reverted`（与改动前同形），宿主原因不进上报——挂账。
- 残留：补报的 8s 兜底对「宿主晚于兜底才应答」会重复落一行日志（回队后又被下一次 flush 送出）——这是兜底对「不丢」的交换，记为可接受。
- 导出同秒撞名消失，回退日志只会在真正不可写时出现。

## Related

- [端口漂移引发的 IPC origin 错配](2026-09-12-port-drift-ipc-origin-mismatch.md)：同一条命令通道的前一个缺陷；其 Decision 1 为**更新块**规定失败分流、Decision 2 把「需重启应用」写进漂移告警，本篇补的是**两个开关行**的三态处置与失败留痕。
- [companion 更新设置页](../feature/2026-08-22-companion-update-settings-section.md)：设置页失败降级契约的所有者。
- [companion invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)：帧形态与失败降级文案的来处。
