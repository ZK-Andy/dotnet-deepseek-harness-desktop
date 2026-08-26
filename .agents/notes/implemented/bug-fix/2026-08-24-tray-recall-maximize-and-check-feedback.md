# Agent Note: tray-recall-maximize-and-check-feedback

Status: implemented

## Problem

v0.3.1 实机复验两项体验缺陷：①**X 隐藏→托盘唤回后窗口丢最大化态**——上游 `RynWindow` 的 Show/Hide 是纯 saucer show/hide 调用，最大化在 unmap/remap 中丢失；恢复需要「记住再恢复」，而查询半边长期缺位。②**托盘菜单「检查更新」没有任何反馈**——菜单项自身无界面，结论本该看设置页状态行，但彼时它正显示降级文案，用户点了像没点。

## Decision

1. **唤回保持最大化（数据源：上游原生 `IRynWindow.IsMaximized`）**：该属性随 [Ryn PR #75](https://github.com/Yupmoh/Ryn/pull/75) 进入 0.30.3/0.30.4（MAXIMIZE 事件镜像缓存 + 启动期从原生窗口同步）。壳侧以它为唯一查询通道：隐藏前采样 `IsMaximized?1:0` 进 `maximizedAtHide`。唤回分两段动作，共用纯函数判据 `TrayRecallMaximize.NeedsMaximize(采样值, 当前值)`：
   - **Linux 隐藏态预置**：ShowAsync 前对未映射窗口发出 `ToggleMaximize`（其内部读原生真值再翻转，不会误伤已最大化态）——GTK 把未映射窗口的 maximize 记为初始态，map 时直接以最大化呈现，消除「先恢复默认尺寸、300ms 后再最大化」的首唤两段式闪变（v0.3.6 实机反馈：首唤必现、后续循环因 GTK 状态已对齐而天然无闪变）。预置失败（deferred 代理异常）记日志退回补正兜底。
   - **唤回后单次补正兜底**：ShowAsync 后延迟一拍（300ms，给 WM 的 remap 与可能迟到的 MAXIMIZE 事件留窗口）再读属性，判定才补一次 `ToggleMaximize`——单次补正不横跳。预置已发则跳过此段：事件镜像可能滞后于真实状态，二次 toggle 会把已最大化的窗口反向还原。
   - 隐藏前未知绝不动作；Win/mac 不做预置（无平台实证，show/hide 行为各异），维持补正路径；样本在唤回末尾一次性消费——残留会让下一次托盘点击（窗口可见时）把用户手动还原的窗口误最大化。
2. **检查结论走系统托盘通知**：`DesktopTrayCommandRouter` 新增可选 `notify` 委托接 `TrayService.ShowNotification`（三平台后端齐全：notify-send/气泡/osascript）；文案映射抽纯函数 `TrayCheckFeedback`——UpToDate/Ready/Error 给结论，Downloading 等中间态不打扰；**仅菜单路径通知**，设置页手动检查不重复。

## Alternatives considered

- **页面视口探测（已服役后退役）**：0.3.2–0.3.3 的现行方案——脚本读 `outerWidth/Height` 对比 `screen.availWidth/Height`，隐藏前采样、唤回后确认仍非最大化才补 toggle。落败原因：①数据源本身不可靠——WebKitGTK/Wayland 下 `window.outer*` 冻结在初始配置尺寸，DevTools 实证探测在目标平台读不到真值；②上游已暴露原生属性，页面级探测的存续理由消失。其「确认后再动」的防横跳语义由判据契约继承。
- **StateChanged 事件自跟踪（已试并否决）**：接口面齐全但 0.3.2 真机无效，Linux 事件通路存疑；`IsMaximized` 属性内部同样依赖事件镜像，但叠加了启动期原生同步与 MAXIMIZE 事件更新，且属上游维护面。
- **向上游提 PR 加 IRynWindow 状态查询**：即 PR #75，2026-08-25 合并并随 v0.30.3 发布——本决策的前置条件。
- **缩短补正延迟减轻闪变**：落败——闪变的根源是「先以普通几何上屏再纠正」的两段式结构，延迟只决定闪变时长，不改变其存在。
- **向上游提 PR 加幂等的 `SetMaximized(bool)`**：可让预置免 toggle 盲区、兜底重复执行无反向还原风险，是更干净的长期形态（PR #75 同款协作先例）；本轮以本地最小改动先行，上游跟进留作候选。
- **Win/mac 一并预置**：落败——两平台无实证且 show/hide 原生行为各异（Windows ShowWindow、macOS NSWindow），盲扩回归面大于收益；维持补正路径待实机证据。
- **检查结论同时发页面 toast**：落败——设置页已有状态行承载，双通道重复打扰；托盘通知只补菜单这条无界面路径。

## Consequences

- `IsMaximized` 是事件镜像而非每次直查原生：若某平台 ShowAsync 重置几何却不发 MAXIMIZE 事件，属性会报过期真值→预置与补正都被跳过（行为退回无补正基线）。此岔路待 Fedora Wayland 实机复验；复现则携实证再议上游。
- 隐藏态预置依赖「GTK 对未映射窗口的 maximize 记为初始态」这一平台行为；若某 Linux 后端静默忽略，该次唤回以普通几何呈现且无补正（预置已发即跳过兜底，防反向还原的取舍）。host.log 的「隐藏态已预置最大化」行是实机复验取证点。
- 全屏不在跟踪范围：`SetFullscreen` 路径不采样不补正，属已知边界。
- notify-send 在 GNOME 依赖通知守护进程（Fedora 工作站默认具备）；缺失环境由 backend 静默吞掉，不影响主流程。
- 测试：`TrayRecallMaximizeTests` 契约 5 例（`NeedsMaximize`，预置与补正共用判据）；文案映射用例不变。实机复验挂账：首唤两段式闪变消除、后续 hide→recall 循环无回归、手动还原后托盘点击不被误最大化。

## Related

- [托盘与关闭最小化](../architecture/2026-08-24-shell-tray-hide-to-tray.md)：hide-to-tray 与中继链路的出处。
- [运行时依赖升级与 arm64 复发](../process/2026-08-25-runtime-deps-upgrade-and-arm64-resume.md)：Ryn 三包 bump 至 0.30.4 与本决策的落地批次。
- [shell 首启加固](2026-08-24-shell-firstboot-hardening.md)：同日托盘顺序契约的前置修复。
- [companion invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)：同轮实机复验的另一缺陷面（0.0.10 勘误）。
