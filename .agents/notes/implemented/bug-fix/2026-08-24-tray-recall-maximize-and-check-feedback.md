# Agent Note: tray-recall-maximize-and-check-feedback

Status: implemented

## Problem

v0.3.1 实机复验两项体验缺陷：①**X 隐藏→托盘唤回后窗口丢最大化态**——上游 `RynWindow` 的 Show/Hide 是纯 saucer show/hide 调用，最大化在 unmap/remap 中丢失；且 `IRynWindow` 只有 ToggleMaximize/SetFullscreen 动作面、没有状态查询属性，「记住再恢复」缺查询半边。②**托盘菜单「检查更新」没有任何反馈**——菜单项自身无界面，结论本该看设置页状态行，但彼时它正显示降级文案，用户点了像没点。

## Decision

1. **唤回保持最大化（0.3.2 复验后重做：事件跟踪 → 视口探测）**：首版订阅 `IRynWindow.StateChanged` 自跟踪，真机无效——Linux 端 maximize→state 事件链不可靠，根因留待上游核查。现行方案以**页面视口探测**为查询通道（`TrayWindowStateProbe`）：脚本读 `outerWidth/Height` 对比 `screen.availWidth/Height`（同为 CSS 像素），隐藏前采样；唤回 ShowAsync 后探测确认仍非最大化才补一次 ToggleMaximize——单次补正不横跳，连续探测不可用则放弃退回旧行为。上游若将来修复 show 保几何，探测读到已最大化会自动跳过，天然向前兼容。
2. **检查结论走系统托盘通知**：`DesktopTrayCommandRouter` 新增可选 `notify` 委托接 `TrayService.ShowNotification`（三平台后端齐全：notify-send/气泡/osascript）；文案映射抽纯函数 `TrayCheckFeedback`——UpToDate/Ready/Error 给结论，Downloading 等中间态不打扰；**仅菜单路径通知**，设置页手动检查不重复。

## Alternatives considered

- **StateChanged 事件自跟踪（已试并否决）**：接口面齐全但 0.3.2 真机无效，Linux 事件通路存疑；视口探测不依赖上游行为，且自带「确认后再动」的防横跳语义。
- **向上游提 PR 加 IRynWindow 状态查询**：原生 `saucer_window_maximized` 查询已在 RynWindow 内部使用、只差公开面——正确解但跨仓节奏慢；探测方案落地后此诉求降级为优化项。
- **检查结论同时发页面 toast**：落败——设置页已有状态行承载，双通道重复打扰；托盘通知只补菜单这条无界面路径。

## Consequences

- 探测容差 16px：合成器在可用区边缘留边框像素按占满处理；真全屏（大于工作区）同样命中，还原形态统一为最大化。
- 探测依赖页面存活与 JS 可执行：恢复屏等场景探测失败按未知处理不动作，行为退回修复前。
- 桥接层对返回值有再序列化（`\u0022` 类转义），解析走 JsonDocument 字符串节点解码——纯解析无反射，PublishAot 安全。
- notify-send 在 GNOME 依赖通知守护进程（Fedora 工作站默认具备）；缺失环境由 backend 静默吞掉，不影响主流程。
- 测试 233/233（+11：文案映射 7、探测判定与解析 4）。

## Related

- [托盘与关闭最小化](../architecture/2026-08-24-shell-tray-hide-to-tray.md)：hide-to-tray 与中继链路的出处。
- [shell 首启加固](2026-08-24-shell-firstboot-hardening.md)：同日托盘顺序契约的前置修复。
- [companion invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)：同轮实机复验的另一缺陷面（0.0.10 勘误）。
