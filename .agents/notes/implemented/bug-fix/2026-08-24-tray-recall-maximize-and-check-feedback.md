# Agent Note: tray-recall-maximize-and-check-feedback

Status: implemented

## Problem

v0.3.1 实机复验两项体验缺陷：①**X 隐藏→托盘唤回后窗口丢最大化态**——上游 `RynWindow` 的 Show/Hide 是纯 saucer show/hide 调用，最大化在 unmap/remap 中丢失；恢复需要「记住再恢复」，而查询半边长期缺位。②**托盘菜单「检查更新」没有任何反馈**——菜单项自身无界面，结论本该看设置页状态行，但彼时它正显示降级文案，用户点了像没点。

## Decision

1. **唤回保持最大化（数据源：上游原生 `IRynWindow.IsMaximized`）**：该属性随 [Ryn PR #75](https://github.com/Yupmoh/Ryn/pull/75) 进入 0.30.3/0.30.4（MAXIMIZE 事件镜像缓存 + 启动期从原生窗口同步）。壳侧以它为唯一查询通道：隐藏前采样 `IsMaximized?1:0` 进 `maximizedAtHide`；唤回 ShowAsync 后延迟一拍（300ms，给 WM 的 remap 与可能迟到的 MAXIMIZE 事件留窗口）再读属性，`TrayRecallMaximize.ShouldRestore(采样值, 当前值)` 判定才补一次 `ToggleMaximize`——单次补正不横跳；隐藏前未知绝不动作；上游 show 保几何时属性为真自动跳过。判定抽纯函数配契约测试。
2. **检查结论走系统托盘通知**：`DesktopTrayCommandRouter` 新增可选 `notify` 委托接 `TrayService.ShowNotification`（三平台后端齐全：notify-send/气泡/osascript）；文案映射抽纯函数 `TrayCheckFeedback`——UpToDate/Ready/Error 给结论，Downloading 等中间态不打扰；**仅菜单路径通知**，设置页手动检查不重复。

## Alternatives considered

- **页面视口探测（已服役后退役）**：0.3.2–0.3.3 的现行方案——脚本读 `outerWidth/Height` 对比 `screen.availWidth/Height`，隐藏前采样、唤回后确认仍非最大化才补 toggle。落败原因：①数据源本身不可靠——WebKitGTK/Wayland 下 `window.outer*` 冻结在初始配置尺寸，DevTools 实证探测在目标平台读不到真值；②上游已暴露原生属性，页面级探测的存续理由消失。其「确认后再动」的防横跳语义由 `ShouldRestore` 契约继承。
- **StateChanged 事件自跟踪（已试并否决）**：接口面齐全但 0.3.2 真机无效，Linux 事件通路存疑；`IsMaximized` 属性内部同样依赖事件镜像，但叠加了启动期原生同步与 MAXIMIZE 事件更新，且属上游维护面。
- **向上游提 PR 加 IRynWindow 状态查询**：即 PR #75，2026-08-25 合并并随 v0.30.3 发布——本决策的前置条件。
- **检查结论同时发页面 toast**：落败——设置页已有状态行承载，双通道重复打扰；托盘通知只补菜单这条无界面路径。

## Consequences

- `IsMaximized` 是事件镜像而非每次直查原生：若某平台 ShowAsync 重置几何却不发 MAXIMIZE 事件，属性会报过期真值→补正被跳过（行为退回无补正基线）。此岔路待 Fedora Wayland 实机复验；复现则回退视口探针或携实证再议上游。
- 全屏不在跟踪范围：`SetFullscreen` 路径不采样不补正，属已知边界。
- notify-send 在 GNOME 依赖通知守护进程（Fedora 工作站默认具备）；缺失环境由 backend 静默吞掉，不影响主流程。
- 测试：`TrayRecallMaximizeTests` 契约 5 例替代原视口判定/解析用例；文案映射用例不变。

## Related

- [托盘与关闭最小化](../architecture/2026-08-24-shell-tray-hide-to-tray.md)：hide-to-tray 与中继链路的出处。
- [运行时依赖升级与 arm64 复发](../process/2026-08-25-runtime-deps-upgrade-and-arm64-resume.md)：Ryn 三包 bump 至 0.30.4 与本决策的落地批次。
- [shell 首启加固](2026-08-24-shell-firstboot-hardening.md)：同日托盘顺序契约的前置修复。
- [companion invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)：同轮实机复验的另一缺陷面（0.0.10 勘误）。
