# Agent Note: tray-recall-maximize-and-check-feedback

Status: implemented

## Problem

v0.3.1 实机复验两项体验缺陷：①**X 隐藏→托盘唤回后窗口丢最大化态**——上游 `RynWindow` 的 Show/Hide 是纯 saucer show/hide 调用，最大化在 unmap/remap 中丢失；且 `IRynWindow` 只有 ToggleMaximize/SetFullscreen 动作面、没有状态查询属性，「记住再恢复」缺查询半边。②**托盘菜单「检查更新」没有任何反馈**——菜单项自身无界面，结论本该看设置页状态行，但彼时它正显示降级文案，用户点了像没点。

## Decision

1. **壳侧自跟踪窗口态**：订阅 `IRynWindow.StateChanged` 维护 Maximized 标志（Minimized 不清标志——最小化再还原仍回最大化，只有主动 Normal 才清）；唤回路径 ShowAsync 后延时 150ms 补一次 ToggleMaximize。
2. **检查结论走系统托盘通知**：`DesktopTrayCommandRouter` 新增可选 `notify` 委托接 `TrayService.ShowNotification`（三平台后端齐全：notify-send/气泡/osascript）；文案映射抽纯函数 `TrayCheckFeedback`——UpToDate/Ready/Error 给结论，Downloading 等中间态不打扰；**仅菜单路径通知**，设置页手动检查不重复。

## Alternatives considered

- **向上游提 PR 加 IRynWindow 状态查询**：正确解但跨仓节奏慢，本地事件跟踪零依赖即时生效；待上游改进落地后在升级验收时重新评估是否撤除补最大化逻辑。
- **JS 视口探测推断最大化**（outerWidth 对比 screen.availWidth）：落败——DPR、多显示器、面板宽度等环境变量过多，误判会直接触发错误的 toggle。
- **检查结论同时发页面 toast**：落败——设置页已有状态行承载，双通道重复打扰；托盘通知只补菜单这条无界面路径。

## Consequences

- 150ms 是 WM 落地几何的经验值，真机若偶发不生效优先调它；**若未来上游修复 show 保几何，本补丁会把已最大化的窗口错误还原**——升级 Ryn 版本时必须重验此项。
- notify-send 在 GNOME 依赖通知守护进程（Fedora 工作站默认具备）；缺失环境由 backend 静默吞掉，不影响主流程。
- 测试 222/222（+7 文案映射用例）。

## Related

- [托盘与关闭最小化](../architecture/2026-08-24-shell-tray-hide-to-tray.md)：hide-to-tray 与中继链路的出处。
- [shell 首启加固](2026-08-24-shell-firstboot-hardening.md)：同日托盘顺序契约的前置修复。
- [companion invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)：同轮实机复验的另一缺陷面（0.0.10 勘误）。
