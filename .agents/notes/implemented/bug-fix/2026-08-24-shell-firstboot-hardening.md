# Agent Note: shell-firstboot-hardening

Status: implemented

## Problem

v0.3.0 Linux rpm 实机验收暴露三项壳层首启缺陷。①**托盘有图标无菜单**：Program 以 SetMenu→Show 顺序就绪化托盘，而 Ryn Linux 后端在 Show 时才注册 StatusNotifierItem，此前 SetMenu 经 `_item?.SetMenu` 静默丢弃（macOS 后端的 RebuildMenu 在 status item 未创建时同类丢弃）；沙箱无 StatusNotifierWatcher，Show 直接抛异常走降级路径，该缺陷在 CI/沙箱不可触达。②**切换日首启横幅消失**：启动横幅任务只等「随包安装尘埃落定」，但安装收尾会触发监督器重启+导航（实机日志 18:30:01 注入 vs 18:30:03 导航完成），横幅注进即将被整体替换的页面。③**诊断导出回退链不闭合**：文档目录解析为空时 `Path.Combine("", …)` 产相对路径，抛出的异常类型不在回退过滤内会整帧失败；且回退提示写 Console，打包产品不可见。

## Decision

1. **托盘顺序契约**：先 `Show()` 后 `SetMenu(…)`，代码注释钉死三平台依据（Linux 注册后才收单、macOS 主线程 FIFO 下 ShowOnUi 先于 RebuildMenu、Windows 右键时才读菜单故两序皆可）；上游侧「缓冲 pending 菜单」的正确修法留作 issue 候选，不阻塞本地。
2. **横幅导航门控**：`RuntimeSupervisor` 新增可选 `onNavigated` 回调（导航委托成功完成后触发）；启动横幅任务的等待条件升级为「安装尘埃落定 ∧（触发过重启则导航落地或 30s 兜底超时）」。重启是否发生由安装任务置位、跨线程经 `Volatile` 读写。
3. **导出回退加固**：文档目录解析为空显式守卫直走回退；回退原因经 log 委托进 host.log；新增可注入文档目录的 internal 重载，配两例回归测试（空目录/不可写→zip 落 `<home>/diagnostics` 且留痕）。顺带清理 TrayTests xUnit2012 警告，恢复 0 警告基线（218/218）。

## Alternatives considered

- **向上游 Ryn 提交 Linux/macOS 后端的菜单缓冲修复**：方向正确但不作为唯一手段——跨仓节奏慢、本地无法控版；本地先以顺序契约止血，上游 issue 由项目主人决定是否提交。
- **横幅注入前延迟固定秒数**：落败——竞速的本质是缺「页面不再被替换」的信号，秒数是猜测且慢机放大、快机白等；TCS 门控 + 超时兜底才是信号语义。
- **为监督器抽象接口以便全环 mock 测试**：落败——为一个回调引入抽象超出需要；仓库既有边界是纯函数单测 + 沙箱端到端，监督器维持此惯例。

## Consequences

- supervisor 构造签名增可选参数，既有调用与测试不受影响；切换日首启的日志时序变化：版本底线检查与旧 home 记录出现在「重启成功」之后（门控后移所致，属预期）。
- 托盘顺序对无 watcher 环境（沙箱/极简 WM）语义不变：仍走初始化失败降级、关窗直退。
- 实机复验清单：右键托盘出菜单且四项动作可用；v0.2.x 升级首启可见旧 home 横幅；拔掉/只读文档目录时导出仍成功并留痕 host.log。

## Related

- [托盘与关闭最小化](../architecture/2026-08-24-shell-tray-hide-to-tray.md)：本笔记修正其「Build 后装菜单并 Show」的顺序表述。
- [共享 home 切换](../architecture/2026-08-23-shared-home-desktop-profile.md)：旧 home 横幅即受门控保护的启动告知之一。
- [诊断可观测性](../architecture/2026-08-24-shell-observability-diagnostics.md)：回退链加固落在其导出器上。
- [companion invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)：同轮实机验收的另一半缺陷面。
