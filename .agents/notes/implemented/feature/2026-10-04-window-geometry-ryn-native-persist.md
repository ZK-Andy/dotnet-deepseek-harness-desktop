# Agent Note: 窗口几何持久化接 Ryn 内建能力

Status: implemented

Review: FULL/2026-10-04/R1=ok R2=ok R3=ok

## Problem

壳重启后主窗口永远回到出厂尺寸（1200×800），用户上一次调整的尺寸/最大化状态全部丢失。原因：`RynOptions` 从未启用几何持久化，窗口每次冷启动按 opts 固定值重建；用户感知上「托盘关掉再打开能记住、重启就不记得」——后者是同一窗口实例 hide/show 的内存残留，并非有任何持久化在生效。跨重启的体验断裂在 0.38.0 升级后已具备零成本修复条件（见 Decision），此前 Ryn 无此能力，壳侧自建是唯一路径。

## Decision

组合根 `BuildApp` 的 `ConfigureOptions` 一处接通 Ryn 0.38.0 内建的 `RynOptions.PersistWindowState`，窗口几何（尺寸/位置/最大化标志）的持久化与恢复全部由上游单点实现承担：

- **保存**：`RynWindow` 原生 move/resize 回调内即时落盘（normal 化尺寸 + `IsMaximized`；最大化时存还原态矩形而非最大化矩形，上游 `Ryn.Core/RynWindow.cs` 窗口态持久化注释的定义），原子写；几何未变动过则无状态文件，行为等同出厂默认。
- **恢复**：`InitializeNative` 建窗期读回——窗口「出生即」正确几何再上屏，无「先默认尺寸后纠正」的两段式闪变；最大化标志一并恢复。Wayland 下坐标由合成器决定（上游 `_usesCompositorPlacement` 分支），尺寸与最大化照常恢复。
- **托盘/退出零接线**：hide-to-tray 与唤回不销毁窗口，天然共享当前几何；托盘退出与普通关窗路径的最终几何已在最后一次 resize/move 事件落盘。托盘唤回的会话内最大化两拍（见 [tray-recall-maximize-and-check-feedback](../bug-fix/2026-08-24-tray-recall-maximize-and-check-feedback.md)）与本决定正交——一个管会话内 hide/recall，一个管跨进程重启，互不替代。
- **落点**：状态文件在 `LocalApplicationData/Ryn/<ApplicationId>/window-state.json`（上游固定），**不进 DSH_HOME**——几何是「本机显示器布局」状态，随机器而非随 profile 互通（与 desktop-preferences.json 进 DSH_HOME 的理由相反）；ApplicationId 的 dev 后缀规则顺带隔离 dev/prod 状态。

壳侧不新增任何窗口状态类型、不新增文件格式、不新增保存/恢复调用点。

## Alternatives considered

- **壳侧自建 WindowStateTracker + 偏好文件（落败）**：按 CloseBehaviorPreference 模式在 dsh home 落 JSON，保存点接托盘 hide 与关窗 Closing，恢复点接启动窗口就绪。落败原因：①完整复刻上游已实现的能力（含最大化还原、原子写、屏幕越界钳制、损坏容错），维护面翻倍；②违反「一个行为一个实现」——托盘 hide 采样、关窗捕获、冷启动恢复三路保存/恢复并存，行为分叉风险恰恰是需求方明确要求避免的；③恢复时点在窗口发布之后，存在先默认尺寸再纠正的闪变，上游建窗期恢复天然无此问题。
- **自建但只管尺寸/最大化、复用 desktop-preferences.json（落败）**：少一个文件。落败原因：该文件由 `CloseBehaviorPreference` 整文件覆写（单键 record），并入窗口几何必须改它的帧形状与写路径，把两个不相关偏好耦进同一写者；省一个文件买来耦合，不值。
- **上游 StateChanged/Resized 事件自跟踪（落败）**：订阅 deferred 代理事件自行累积当前几何。落败原因：与已否决的镜像门控同型风险——事件通路在 Linux 实机上有不可靠前科（Fedora Wayland 实证 `IsMaximized` 镜像不可信，见 tray-recall-maximize ADR Alternatives），且仍需自建恢复与落盘，相对上一条没有减负。

## Consequences

- 买到的：跨重启几何连续性（尺寸/最大化），零壳侧维护面；上游后续对钳制/多屏/兼容性的修复随包升级自动获益。
- 付出的：状态文件脱离 DSH_HOME 家族，诊断时需到 `LocalApplicationData/Ryn/<appId>/` 找它（`desktop-preferences.json` 之外多一个位置）；几何恢复语义（normal 态矩形、Wayland 坐标交给合成器）以 0.38.0 上游实现为准，壳侧不可调。
- 关窗即退且全程未动过几何的窗口没有状态文件，冷启动维持出厂默认——预期行为而非缺陷。

## Related

- [tray-recall-maximize-and-check-feedback](../bug-fix/2026-08-24-tray-recall-maximize-and-check-feedback.md)——会话内 hide/recall 最大化保持；与本决定分工见 Decision 第 3 条。
- [companion-settings-opencode-styling-and-close-to-tray](../feature/2026-08-25-companion-settings-opencode-styling-and-close-to-tray.md)——desktop-preferences.json 落位 DSH_HOME 的先例；本决定为何反向（机器本地）见 Decision 落点条。
