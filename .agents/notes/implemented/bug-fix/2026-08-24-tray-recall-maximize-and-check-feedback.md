# Agent Note: tray-recall-maximize-and-check-feedback

Status: implemented

## Problem

v0.3.1 实机复验两项体验缺陷：①**X 隐藏→托盘唤回后窗口丢最大化态**——上游 `RynWindow` 的 Show/Hide 是纯 saucer show/hide 调用，最大化在 unmap/remap 中丢失；恢复需要「记住再恢复」。②**托盘菜单「检查更新」没有任何反馈**——菜单项自身无界面，结论本该看设置页状态行，但彼时它正显示降级文案，用户点了像没点。①的迭代中还叠加两个 Linux 实机问题：首唤闪变（v0.3.6 反馈：窗口先以默认尺寸上屏、300ms 后才被纠正为最大化）与镜像门控失效（Fedora Wayland 三次实验零日志实证 `IsMaximized` 事件镜像在 show 重置几何后不可信，预置判据永不触发）。

## Decision

1. **唤回保持最大化**：隐藏前采样原生 `IRynWindow.IsMaximized`（[Ryn PR #75](https://github.com/Yupmoh/Ryn/pull/75)，0.30.3 起）进 `maximizedAtHide`（1=最大化/0=非最大化/-1=采样异常）；动作面统一为 [Ryn PR #79](https://github.com/Yupmoh/Ryn/pull/79) 的幂等 `SetMaximized(bool)`（0.30.5 起，直调原生 `saucer_window_set_maximized` 不读缓存），调用点不读取镜像。纯函数判据 `TrayRecallMaximize.ShouldEnsure(采样值)` 两拍共用：
   - **Linux 隐藏态预置**：ShowAsync 前 `ShouldEnsure` 即 `SetMaximized(true)`——GTK 把未映射窗口的 maximize 记为初始态，map 时直接以最大化呈现，消除首唤两段式闪变。判据只看隐藏前采样，无镜像门控；预置失败（deferred 代理未就绪）记日志，兜底确认仍在。
   - **唤回后兜底确认**：ShowAsync 后延迟 300ms 再 `SetMaximized(true)`——目标态幂等，预置已生效时是原生层 no-op，无需任何「预置已发则跳过」守卫。Win/mac 无预置（平台 show/hide 行为各异且无实证），全靠此拍完成保持。
   - 隐藏前未知绝不动作；样本在唤回末尾一次性消费——残留会让下一次托盘点击（窗口可见时）把用户手动还原的窗口误最大化。
2. **检查结论走系统托盘通知**：`DesktopTrayCommandRouter` 新增可选 `notify` 委托接 `TrayService.ShowNotification`（三平台后端齐全：notify-send/气泡/osascript）；文案映射抽纯函数 `TrayCheckFeedback`——UpToDate/Ready/Error 给结论，Downloading 等中间态不打扰；**仅菜单路径通知**，设置页手动检查不重复。

## Alternatives considered

- **镜像门控预置 + 条件 toggle 补正（已服役后退役）**：v0.3.7 形态——预置与补正都以 `NeedsMaximize(采样, IsMaximized)` 为门、动作用 `ToggleMaximize`，并靠「预置已发则跳过补正」防二次 toggle 反向还原。落败原因：①目标机实证镜像不可信，门控让预置永不触发=闪变复发；②幂等 set 让防横跳守卫与镜像读取整体失去存在理由。
- **页面视口探测（已服役后退役）**：0.3.2–0.3.3 的方案——脚本读 `outerWidth/Height` 对比 `screen.availWidth/Height`，隐藏前采样、唤回后确认仍非最大化才补 toggle。落败原因：①数据源本身不可靠——WebKitGTK/Wayland 下 `window.outer*` 冻结在初始配置尺寸，DevTools 实证探测在目标平台读不到真值；②上游已暴露原生属性，页面级探测的存续理由消失。
- **StateChanged 事件自跟踪（已试并否决）**：接口面齐全但 0.3.2 真机无效，Linux 事件通路存疑；`IsMaximized` 属性内部同样依赖事件镜像，但叠加了启动期原生同步与 MAXIMIZE 事件更新，且属上游维护面。
- **向上游提 PR 加 IRynWindow 状态查询**：即 PR #75，2026-08-25 合并并随 v0.30.3 发布——本决策的前置条件。
- **向上游提 PR 加幂等的 `SetMaximized(bool)`**：即 PR #79，2026-08-26 合并并随 v0.30.5 发布——动作面收敛为本决策现行形态的直接前置。
- **缩短补正延迟减轻闪变**：落败——闪变的根源是「先以普通几何上屏再纠正」的两段式结构，延迟只决定闪变时长，不改变其存在。
- **Win/mac 一并预置**：落败——两平台无实证且 show/hide 原生行为各异（Windows ShowWindow、macOS NSWindow），盲扩回归面大于收益；维持唤回后兜底路径待实机证据。
- **检查结论同时发页面 toast**：落败——设置页已有状态行承载，双通道重复打扰；托盘通知只补菜单这条无界面路径。

## Consequences

- `IsMaximized` 镜像的唯一读点是隐藏前采样；该读数失真（报非最大化）时行为退化为「不确保最大化」基线；镜像滞后最多造成一次冗余的第二拍（原生层 no-op），不存在错误动作通路。
- 唤回的确认延迟窗内若穿插新的 hide，样本会被唤回末尾的消费清成未知——下一次唤回退化为不确保最大化的基线（降级而非腐化）；幂等两拍使并发唤回本身无害。
- 隐藏态预置依赖「GTK 对未映射窗口的 maximize 记为初始态」这一平台行为；若某 Linux 后端静默忽略，兜底确认在 300ms 后补齐——幂等语义下两拍同发无副作用。host.log 的「隐藏态已预置最大化」行仍是实机取证点。
- 全屏不在跟踪范围：`SetFullscreen` 路径不采样不确认，属已知边界。
- notify-send 在 GNOME 依赖通知守护进程（Fedora 工作站默认具备）；缺失环境由 backend 静默吞掉，不影响主流程。
- 测试：`TrayRecallMaximizeTests` 契约 3 例（`ShouldEnsure`）。实机复验挂账：首唤两段式闪变消除、hide→recall 循环无回归、手动还原后托盘点击不被误最大化。

## Related

- [托盘与关闭最小化](../architecture/2026-08-24-shell-tray-hide-to-tray.md)：hide-to-tray 与中继链路的出处。
- [运行时依赖升级与 arm64 复发](../../archived/process/2026-08-25-runtime-deps-upgrade-and-arm64-resume.md)：Ryn 三包 bump 至 0.30.4 与本决策的落地批次（0.30.5 bump 同线延续；已归档冻结）。
- [shell 首启加固](2026-08-24-shell-firstboot-hardening.md)：同日托盘顺序契约的前置修复。
- [单实例 launcher 激活](../architecture/2026-08-26-single-instance-launcher-activation.md)：launcher 激活唤起是采样链之外的独立显示入口（消费样本但不做预置/确认），勿纳入本线回归清单。
