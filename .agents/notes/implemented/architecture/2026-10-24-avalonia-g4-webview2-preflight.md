# Agent Note: Windows WebView2 运行时启动前探测（Avalonia G4-b）

Status: implemented

Review: FULL/2026-10-24#2/R1=ok R2=ok R3=ok

## Problem

Avalonia 宿主在 Windows 上依赖系统 Microsoft Edge WebView2 运行时。运行时缺失（或安装目录里的运行时文件不在位）时，`NativeWebView` 在 WebView2 COM 初始化处失败——此时主窗已创建、Avalonia 生命周期已开始，失败落在原生回调/异步续体里；用户拿不到可操作原因（该失败形态本机未真机取证，属推断）。现有提示只有 Inno 安装器在 `ssPostInstall` 读 `Clients\pv` 后的一次 `MsgBox`：覆盖不到「装完之后运行时被清理/卸载/更新损坏」的路径；`pv` 只证明装过、不证明文件在位；提示本身不可操作（只给文字）。

## Decision

- **探测点在 Avalonia 生命周期与 dsh spawn 之前**（`AvaloniaHost.Run` 中 `CreateSharedState` 之后）：先取 UiLocale 再探测；缺失即返回退出码 1，不建窗、不 spawn、不起回环代理。
- **判据与 Avalonia 控件 `ManagedWebView2Loader` 同源**（不是安装器的 `Clients\pv`）：`WEBVIEW2_BROWSER_EXECUTABLE_FOLDER` 覆盖优先 → HKLM（Registry32）五个通道 → HKCU 五个通道的 `EdgeUpdate\ClientState\<uuid>` 的 `EBWebView` 安装目录 → `EBWebView\<arch>\EmbeddedBrowserWebView.dll` 文件存在。壳判定「可用」与控件「找得到」是同一条件。
- **fail-open 单点**：探测异常不在适配器吞掉，统一由 `WebView2StartupGuard` 捕获，按可用放行并留痕——探测缺陷不能变成启动阻断；真缺时仍有既有失败面兜底。
- **提示用无属主 `MessageBoxW`**（是/否 + 错误图标 + 置前）：是 = 经 `ISystemBrowser` 打开 Evergreen 下载页，然后退出；否/关闭框 = 直接退出。两种选择都不放行（窗口无法渲染）。
- **端口与适配**：`IWebViewRuntimeProbe` 落 Core（R3 边界），注册表读取落 Infrastructure（`WebView2RuntimeProbe`），提示编排在 `WebView2StartupGuard`（不碰 Avalonia/Win32 类型，headless 可测）；Windows 接线集中在 `WebView2Registration`（`*Registration.cs`，R1 注册豁免），`AvaloniaHost.Run` 只加一次守卫调用。
- **文案进 `UiCopy`**（`WebView2MissingTitle` / `WebView2MissingBody`），中英双语；`verify-ui-copy` 的消费清单纳入 guard 文件。

## Alternatives considered

- **只保留安装器提示**：覆盖不到「装完后运行时被清理/卸载/更新损坏」，且 `pv` 不保证文件在位。落败。
- **复用进程内 `FailureDialogWindow`**：需要先起 Avalonia 生命周期与一个属主窗口，恰是缺 WebView2 时要避免的路径；还要为「无主窗」加一条启动分支。落败。
- **只查 `Clients\pv`**：装过 ≠ 可用（UniGetUI 的教训是缺失时回退旧 EdgeHTML 会在 `async void` 续体里崩进程）；与控件判据分叉会制造「壳说可用、控件找不到」的假阴性。落败。
- **调 `Microsoft.Web.WebView2` 的 `GetAvailableBrowserVersionString`**：Avalonia 不引用该包，需新增包引用并 P/Invoke `WebView2Loader.dll`（G5 摘 Ryn 后产物不再带它）。落败。
- **反射 Avalonia 内部 `ManagedWebView2Loader`**：依赖未公开实现，升级即碎。落败。
- **捆绑固定版本 WebView2 运行时**：+100MB 级体积与更新负担。落败。

## Consequences

- Windows 运行时缺失（判据＝`EmbeddedBrowserWebView.dll` 不在位）时得到可操作提示，退出码 1；不再落到 WebView 创建处的原生失败。
- 判据是控件解析实现的复刻：通道 UUID 表、ClientState 子键与 `EBWebView` 值名由单测逐字钉住；Avalonia 升级若改解析次序，本探测须同批跟进（探测注释指明同源对象）。
- 提示是 Win32 原生框（非 Avalonia 主题、按钮文案随系统语言）：这是缺 WebView 时唯一不依赖待检对象的通道，产品文案不自绘。
- 判据只判「文件在位」：注册表可读但 dll 损坏/依赖缺失的极端情形仍会在建窗时失败，由既有失败面兜底（guard 的 fail-open 只覆盖探测异常，不把该情形说成可启动）。
- 真机验证未做（本机无 Windows 桌面）：判据由源码对照与单测固定；「缺运行时 → 提示 → 装完 → 正常启动」仍列 G4-b 真机验收项。

## Testing

- 单测：`WebView2StartupGuardTests`（8 例：可用不弹框、缺失两分支、英文文案、探测异常 fail-open、提示异常/浏览器异常/浏览器未开不崩）；`WebView2RuntimeProbeTests`（11 例：通道表与顺序、ClientState 子键、EBWebView 值名、架构映射、未知架构防御性缺省、dll 布局、空目录）。
- `dotnet build` 0 警告；`dotnet test` 全绿（新增 19 例；计数基线以 `scripts/test-baseline.json` 为准）。
- 门禁：compose-root / registration-discipline / code-health / code-conventions / ui-copy / increment 全绿。

## Related

- [avalonia-host-migration](2026-10-22-avalonia-host-migration.md)：阶段表 G4 与「Windows 依赖 WebView2 运行时」
- [avalonia-g4-packaging-surface](2026-10-24-avalonia-g4-packaging-surface.md)：把本片推迟到 G4-b 的决定与理由
- [avalonia-g1g3-recovery-bootstrap-supervision](2026-10-23-avalonia-g1g3-recovery-bootstrap-supervision.md)：失败面承载
