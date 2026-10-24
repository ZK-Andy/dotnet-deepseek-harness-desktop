# Agent Note: 桌面验收走 headless 与协议面，取消人工点检

Status: implemented

Review: FULL/2026-10-24#3/R1=ok R2=ok R3=ok

增量闸豁免：gross ≤ 1100，fanout ≤ 15，packages ≤ 7（本批新增 headless 测试工程四文件与测试三片同批落地，拆开会留下缺测试工程或缺用例的不可验中间态。packages 按「新增行内 distinct id」口径计 7：净新增仅 Avalonia.Headless.XUnit + xunit.v3 两个，其余五个（Microsoft.NET.Test.Sdk / coverlet.collector / StyleCop.Analyzers / Microsoft.VisualStudio.Threading.Analyzers / xunit.runner.visualstudio）为仓内既有依赖在新工程的重现，其中 xunit.runner.visualstudio 经 VersionOverride 落 3.1.5 与 v2 侧 4.0.0 并存）。

## Problem

四类 GUI 验收（托盘、无边框顶栏拖拽/缩放、WebView2 缺失提示、恢复页/首启引导）长期以「真机人工点检」形式挂账：环境缺位（无 macOS/Windows 桌面、Linux 桌面会话不稳），条目却在账上显示「待验」。人工检查不可作为门——不可复现、不可审计、不进回归，且事实上没有执行记录。

同时 CI 已具备真实内核（三平台全量测试）与真实截图（Linux `Xvfb + scrot`、macOS `screencapture`、Windows `CopyFromScreen`），但截图只证终态「装得上、起得来、有像素」，不证交互。

## Decision

1. **采用框架自带的测试平台**：`Avalonia.Headless` + `Avalonia.Headless.XUnit`（12.1.4，与仓内 Avalonia 同版）+ Skia 离屏（`UseSkia()` 且 `UseHeadlessDrawing=false`，否则 `CaptureRenderedFrame()` 返回 null）+ xunit.v3 3.2.2（Avalonia.Headless.XUnit 12.1.4 nuspec 依赖上限，4.x 在发现阶段 `MissingMethodException`）。全程无需显示服务器，三平台 CI 可跑。
2. **测试工程走 VSTest 形态，不走 MTP**：xunit.v3 的自含 runner（Exe + Microsoft.Testing.Platform）被 `dotnet test` **静默跳过**——项目级与 slnx 级均 exit 0 且零输出（本地 .NET SDK 10.0.400 实证）；MTP 模式须 global.json 全仓选入，会波及既有 VSTest 覆盖收集管道，落败。落地形态：测试工程保持 VSTest（Library + `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio` 3.1.5 经 `EnableVersionOverride` 绕 CPM 同 id 双主版本，v2 侧 4.0.0 不受影响）；slnx 级 `dotnet test` 五工程全被发现，覆盖收集经 `coverlet.collector`（与其余测试工程同形——缺它时 `--collect:"XPlat Code Coverage"` 对本工程「找不到数据收集器」仅告 warning、静默零覆盖）接进既有 cobertura 管道；基线口径家 [coverage-baseline-from-ci-cobertura](2026-09-12-coverage-baseline-from-ci-cobertura.md) 与并集口径 [coverage-baseline-multi-project-merge](2026-09-14-coverage-baseline-multi-project-merge.md) 不变，本工程入列后的基线/徽章跟值随既有记账待办执行。
3. **测试应用加载 Fluent 控件主题**（与生产 `ShellApplication` 同源 `new FluentTheme()`）：空应用无主题时 Button/TextBox 无模板——控件没有可命中的视觉子树，鼠标点击静默失效（键盘语义 IsDefault/IsCancel 不受影响）。坑记入 cookbook。
4. **原生面不做 OS 级输入模拟**：拖拽/缩放全链真件断言——页面命令帧 → `WindowCommandRouter` → `AvaloniaWindowCommands`（CSS→设备像素换算，`SetRenderScaling(2)` 验证）→ 假 `IWindowDrag` 记录 `TryBeginMove`/`TryBeginResize` 与坐标；非法参数回错误帧且不触端口，端口拒绝仍回成功帧。
5. **取消「真机验收」待办**：落地映射——V1 地基（窗口构造/输入路由/帧捕获）、V2 失败面模态（`FailureDialogWindow` ShowDialog 生命周期、退出=true/留下=false/Esc 缺省 false）；恢复页与 WebView2 探测分支已由既有端口化单测覆盖（`RecoveryPageBuilderTests`/`WebView2StartupGuardTests`），headless 不重复。cgroup 委派的命令构造已有 `DshSubprocessScopeReaperTests` 覆盖；CI 环境能力探针未单列，见残留清单。观测不到的层写成残留代价，不再作为待办漂着。

## Alternatives considered

- **平台级输入注入（xdotool / SendInput / CGEvent）**：三套平台代码，依赖窗口管理器、辅助功能授权与会话形态，脆弱且与框架自带测试平台重复。落败。
- **Playwright / Appium / FlaUI**：为 Electron、WPF、移动端设计；本仓是 Avalonia + 原生 WebView 组合，驱动面不对位，且引入重外部依赖。落败。
- **保留人工点检**：不可复现、不可审计，本轮之前四条挂账无执行记录——已经证明不可行。落败。
- **只靠 Xvfb 截图**：只证终态、不证交互，对托盘与原生对话框没有观测面。不单独成立，保留为页面内容的补充证据。
- **xunit.v3 MTP 形态 + global.json 全仓选入 MTP 模式**：MTP 工程被缺省 `dotnet test` 静默跳过已实证；全仓切 MTP 模式则 CI 的 `--collect:"XPlat Code Coverage"`（VSTest 数据收集器语法）面临管道重验，波及面远超本批。落败。
- **headless 下测 Linux 托盘 D-Bus 协议面**（原计划 V3 组成）：Avalonia headless 平台无 `TrayIcon` 实现（stubs 仅剪贴板/光标/字体/图标/屏幕），Avalonia 自带的 FreeDesktop DBus 托盘后端不注入 headless 平台，协议面在 headless 不可达。落败——菜单模型语义由既有 `ShellTrayActionTests` 覆盖，图标本体记残留。

## Consequences

- 新增一个测试工程，xunit v3 与仓内现有 `xunit 2.9.3` 并存（独立程序集，不迁移旧工程）；`xunit.runner.visualstudio` 同 id 双主版本经 `EnableVersionOverride` 并存，两个版本互不牵动；Avalonia 升级时 headless 包与主题版本须同步。
- headless 没有 `NativeWebView`：页面内容、`window.__shell` 桥与注入脚本不由此获得自动化覆盖，仍靠 Xvfb 冒烟。
- 残留不验面（明确记录，不再挂账）：窗口被真实移动/缩放的最终结果、系统托盘图标的实际像素与点击命中（D-Bus 注册层在 headless 不可达）、macOS 辅助功能（TCC）下的注入、CI 三平台环境能力探针（user manager/委派子树）未单列。
- 测试工程引用主工程（`InternalsVisibleTo` 已接），后续壳 UI 新面按 V2/V3 模式就近补 headless 用例。
