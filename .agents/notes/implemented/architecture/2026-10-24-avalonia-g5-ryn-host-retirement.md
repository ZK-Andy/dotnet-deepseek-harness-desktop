# Agent Note: Avalonia G5——旧 Ryn 宿主下线

Status: implemented

Review: FULL/2026-10-24#3/R1=ok R2=ok R3=ok

## Problem

宿主自 G0 起已切到 Avalonia（`Program` → `Hosting.AvaloniaHost`），但旧 Ryn 宿主仍完整编译、打包并发运：

- 旧装配面（`DesktopAdapter*`、`Bootstrap/StartupSequence*`、`RuntimeResolutions`、按域 `AddXxx` DI 注册）与旧页面适配器（`RynNavigationCallbacks`、`WebViewNavigator`、`WindowScriptTarget`、`CaptionBarInstaller`）在运行期不可达，却持续占用门禁在册面与评审面；
- `Ryn*` 五个包（宿主、托盘插件、对话框插件、回调源生成、IPC 源生成）与 `ryn.json` 仍在产物里，`ICommandRouter` / `RynCommandNotFoundException` 这两个命令契约类型来自 `Ryn.Ipc`——摘包被这一层硬耦合挡住；
- 打包断言要求 `runtimes/<rid>/native` 存在，而该目录只由 Ryn 包供给，包一摘断言即成空转；
- companion 插件仍只认 `window.__ryn`（G2 把消费方迁移推到 G3，G3 未落），Avalonia 宿主只注入 `window.__shell`——插件在真实页面里拿不到命令通道；
- 版本底线与非受控退出横幅只挂在旧编排器上，Avalonia 宿主没有接（此前记在待办里的「横幅无落点」实为未接线）；
- 文档（architecture / development / testing / cookbook / architecture-standards / README 双语）仍按双宿主叙述，`ryn.json` 与 Ryn 能力沙箱被当作现状。

## Decision

**旧 Ryn 宿主整体下线，命令契约迁回 Core，运行面只保留 Avalonia 一条路径。**

- **命令契约**：`ICommandRouter` 与 `CommandNotFoundException` 落 `Core/Ipc`（与 `HostCommandIds` 同族）；`RouteAsync` 去掉无人使用的 `IServiceProvider` 参数（分发点从不经容器解析）。未注册命令由 `ShellBridge` 直接回错误帧，deny-by-default 的等效面收窄为「命令注册表」。
- **删除**：`DesktopAdapter*`、`Bootstrap/StartupSequence*`、`RuntimeResolutions`、`RunServicesRegistration`、`BootstrapRegistration`、`RecoveryRegistration`、`UpdateRegistration`、`Tray/{TrayController,TrayRegistration,DesktopTrayCommandRouter}`、`RynNavigationCallbacks`、`RynPageBridgeAdapters`、`CaptionBarInstaller`、`WebViewNavigator`、`WindowScriptTarget`、`IPageBridgePorts`、`RynOptionsProfile`、`RynFailureDialog`、`ryn.json`、`DialogOrphanReaper`、`AppExitOrchestration`、`DialogToolAvailability`，及仅供上述代码使用的测试（含 `CompositionRootSequenceTests`、`RynWindowCapabilityTests`、`TrayTests`）。
- **保留的共用件**：`CloseGate`、`TrayMenuActions`（只剩条目模型与 id）、`TrayRecallMaximize`、`TrayCheckFeedback`、`CloseToTrayCommandRouter`；`RynProbeValue` 改名 `ScriptProbeValue`（文件与测试同步改名，行为不变）。
- **包与协议**：删 `Ryn` / `Ryn.Plugins.Tray` / `Ryn.Plugins.Dialog` / `Ryn.Callbacks` / `Ryn.Ipc.Generator` 五个包引用；`HostCommandIds.TrayEvent`（`desktop.tray.event`）删除——Avalonia 托盘是原生回调，页面侧没有事件源；companion 中继随之移除。
- **companion 迁到 `__shell`**：`client.js` 与测试工具链（`testkit/harness.js`）的命令通道、事件订阅、外链失败 toast 全部改走 `window.__shell`；装配面对桥晚注入做有界等待（50ms × 40），不再一次性跳过。G2 推迟、G3 漏掉的这项在本片闭合。恢复页与顶栏脚本去掉 `__ryn` 回落；`CaptionBarChannel` 双通道 record 整体删除（脚本只走 `__shell`），`window.__shell_titlebar` 随之停止发布。插件版本 0.0.22 → 0.0.23（否则 profile 里已装的旧 client 会被同版判定跳过）。
- **启动期告知接进新宿主**：`StartupNoticeService` 在 `ShellWiringRegistration` 构造，窗口起步收口后由 `ShellApplication` 启动；两条横幅走 `PagePump.ShowBannerWhenShellReadyAsync`（holder 落点判据），版本探测与脏退判定口径不变。
- **门禁重绑**：`verify-compose-root.py` 根集改为 `Program.cs` + `Hosting/AvaloniaHost.cs`，装配面增 `Hosting/ShellWiringRegistration.cs`；C4（两阶段同步桥）与 C6（排序器形状）随其对象（`DesktopAdapter` 桥、`StartupSequence`）退役，编号保留；C5/C7 清单同步；`verify-code-health.py` 的 F3 根集同口径；`scripts/domain-files.manifest.json` 与 `verify-package-layout.sh` 的 `runtimes/` 断言同步删除。`AvaloniaHost.Run` 为守 F4 拆出 `AcquireSingleInstance` / `AcquireRunMarker` / `EnsureWebView2Runtime` / `DisposeShell`（均为装配头职责，C2 在册）。
- **配置**：删 `CaptionBarRegisterSeconds`、`BannerMaxAttempts` 与 appsettings 的 `Ryn` 节（消费方随旧路由/旧桥一并删除）。
- **文档**：`docs/architecture.md`、`development.md`、`testing.md`、`cookbook.md`、`architecture-standards.md`、README 双语改按单一 Avalonia 宿主叙述；`ryn.json` / Ryn 能力沙箱 / `RynOptions` 相关表述删除；旧宿主 ADR 补 Erratum，迁移 ADR 转 implemented。

增量闸豁免：gross ≤ 6500，fanout ≤ 15（本片以删除为主，`+988/−5498`、161 文件；拆开会留下「旧宿主已删、契约仍在 Ryn 包」的不可编译中间态）。

## Alternatives considered

- **只摘包、不删旧宿主**（保留 `DesktopAdapter*` 作回滚）：`Ryn.Ipc` 的 `ICommandRouter` 会迫使包留下，等于什么都没摘；回滚早已由 `v0.6.14` tag 提供，代码里再留一份双轨只增加双份维护面。落败。
- **保留 `__ryn` 别名兼容层**：G2 已判定双通道会让迁移面模糊；页面侧只剩一个消费方（companion），改名成本低于长期养一个假名。落败。
- **只删代码、不接启动期告知**：表面「旧宿主下线」，实际把版本底线/脏退提示一起带进坟墓，0.6.14 parity 不成立。落败。
- **保留 C4/C6 但改成恒真**：空跑的判据会让人以为装配面仍被两阶段桥/排序器约束；与其留空壳，不如随对象退役并保留编号引入处。落败。
- **保留 `runtimes/` 断言**：摘包后该目录恒空，断言只会把打包腿永久判红或诱导手工造目录。落败。
- **把门禁根集继续指向 `DesktopAdapter*`**：根集里只剩 `Program.cs` 时 C3/C5 失去被检对象，门禁变成假绿。落败。

## Consequences

- 运行面只剩一条宿主路径；产物不再含 Ryn 原生库与 `ryn.json`，Windows 产物不再带 `WebView2Loader`（`ManagedWebView2Loader` 定位已装运行时）。
- **行为变化**：companion 的更新入口/设置区/locale 桥/报障/外链失败 toast 在 Avalonia 页面重新可用（此前 `__ryn` 不存在即整块跳过）；版本底线与非受控退出横幅在真 UI 真正出现（此前只有旧宿主接线）。两项都属 parity 修复，不是新能力。
- 测试面 1222 → 1111 例（删除旧宿主/旧诊断路径测试 111 例；保留路径的用例改写并入既有文件）；覆盖率 66.86%（7413/11088，CI `40063115574`），基线/徽章已跟值。
- 真机验收仍挂账（本机无桌面/Windows）：托盘、恢复页/引导、WebView2 缺运行时提示、无边框顶栏四项与迁移前同口径；本片未新增真机承诺。
- `verify-compose-root` 的 C2 名单从此包含 `Hosting/ShellWiringRegistration` 的 `RunWiring`（记录类型声明按既有正则计成员）；新增装配成员仍须显式在册。
- 0.6.14（Ryn 宿主）仍是回滚产物；0.7.0 发版流程不在本片内。

## Testing

- `dotnet build` 0 警告 0 错误；`dotnet test` 全绿：Desktop 278 / Core 223 / Infrastructure 604 / Analyzers 6（合计 1111）。
- 门禁：`verify-compose-root.py --enforce`（含 `--self-test` 重写）、`verify-code-health.py --enforce`、`verify-registration-discipline.py --enforce`、`verify-code-conventions.py --enforce`、`verify-host-command-ids.py --enforce`、`verify-core-purity.py --enforce`、`verify-ui-copy.py`、`verify-package-layout.sh --self-test`、`verify-shell-standards.sh` 全绿。
- companion：`node --test`（`plugins/dsh-desktop-companion`）20 例通过（托盘转发用例随中继删除；新增桥晚注入用例）。
- 未覆盖：三平台真机托盘/顶栏/恢复页/WebView2 提示；CI 覆盖率跟值。

## Related

- [avalonia-host-migration](2026-10-22-avalonia-host-migration.md)：阶段表 G5 与「迁移期间双宿主并存」的收尾
- [ryn-shell-bundled-dsh-runtime](../../archived/architecture/2026-08-20-ryn-shell-bundled-dsh-runtime.md)：被本片替换的宿主选型（已补 Erratum）
- [avalonia-g2-shell-bridge](2026-10-22-avalonia-g2-shell-bridge.md)：`__shell` 通道契约与 companion 迁移的推迟记录
- [avalonia-g3-update-surface](2026-10-23-avalonia-g3-update-surface.md)：holder 落点判据与横幅承载
- [avalonia-g1g3-recovery-bootstrap-supervision](2026-10-23-avalonia-g1g3-recovery-bootstrap-supervision.md)：恢复页/监督/引导接线
