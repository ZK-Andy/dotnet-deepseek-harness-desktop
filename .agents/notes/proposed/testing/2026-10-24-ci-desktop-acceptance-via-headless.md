# Agent Note: 桌面验收走 headless 与协议面，取消人工点检

Status: proposed

## Problem

四类 GUI 验收（托盘、无边框顶栏拖拽/缩放、WebView2 缺失提示、恢复页/首启引导）长期以「真机人工点检」形式挂账：环境缺位（无 macOS/Windows 桌面、Linux 桌面会话不稳），条目却在账上显示「待验」。人工检查不可作为门——不可复现、不可审计、不进回归，且事实上没有执行记录。

同时 CI 已具备真实内核（三平台全量测试）与真实截图（Linux `Xvfb + scrot`、macOS `screencapture`、Windows `CopyFromScreen`），但截图只证终态「装得上、起得来、有像素」，不证交互。

## Proposal

1. **采用框架自带的测试平台**：`Avalonia.Headless` + `Avalonia.Headless.XUnit`（12.1.4，与仓内 Avalonia 同版）+ Skia 离屏（`UseSkia()` 且 `UseHeadlessDrawing=false`，否则 `CaptureRenderedFrame()` 返回 null）+ `xunit.v3` 3.2.2（该包 nuspec 的依赖上限，4.x 在发现阶段 `MissingMethodException`）。已在本仓实跑验证：窗口构造、鼠标事件经视觉树路由到控件、离屏帧捕获，全程无需显示服务器。
2. **原生面不做 OS 级输入模拟**：顶栏拖拽/缩放断言端口调用（假 `IWindowDrag` 记录 `TryBeginMove`/`TryBeginResize` 与坐标）；Linux 托盘断言 `org.kde.StatusNotifierItem` 注册与 `com.canonical.dbusmenu` 菜单项并可点击，Win/mac 断言对象状态。
3. **取消「真机验收」待办**：能自动化的转成 CI 断言；窗口系统接管的拖拽结果、托盘图标像素、macOS 辅助功能注入这类观测不到的层，写成残留代价，不再作为待办漂着。
4. **cgroup 委派**：命令构造与生命周期进单测；环境能力（user manager、委派子树）由 CI 环境探针实测（V0），不靠推断。

## Alternatives considered

- **平台级输入注入（xdotool / SendInput / CGEvent）**：三套平台代码，依赖窗口管理器、辅助功能授权与会话形态，脆弱且与框架自带测试平台重复。落败。
- **Playwright / Appium / FlaUI**：为 Electron、WPF、移动端设计；本仓是 Avalonia + 原生 WebView 组合，驱动面不对位，且引入重外部依赖。落败。
- **保留人工点检**：不可复现、不可审计，本轮之前四条挂账无执行记录——已经证明不可行。落败。
- **只靠 Xvfb 截图**：只证终态、不证交互，对托盘与原生对话框没有观测面。不单独成立，保留为页面内容的补充证据。

## Consequences

- 新增一个测试工程，`xunit.v3` 与仓内现有 `xunit 2.9.3` 并存（独立程序集，不迁移旧工程）；Avalonia 升级时两处版本须同步。
- headless 没有 `NativeWebView`：页面内容、`window.__shell` 桥与注入脚本不由此获得自动化覆盖，仍靠 Xvfb 冒烟。
- 残留不验面（明确记录，不再挂账）：窗口被真实移动/缩放的最终结果、系统托盘图标像素与命中、macOS 辅助功能（TCC）下的注入。
- 需要把宿主端口（`IWindowDrag` 等）以假件暴露给测试；可能触及 `InternalsVisibleTo` 与注册纪律门禁，逐片评估。
