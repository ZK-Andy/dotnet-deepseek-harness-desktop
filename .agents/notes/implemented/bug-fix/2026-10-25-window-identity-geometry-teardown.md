# Agent Note: 真机修复批——窗口身份（WM_CLASS/图标）、几何持久化重建与退出摘 WebView

Status: implemented

Review: FULL/2026-10-25/R1=ok R2=ok R3=ok

## Problem

v0.7.0 linux 真机验收暴露三个确定性缺陷（2026-10-25，journal + coredumpctl + xprop 实证）：

1. **启动器/Alt-Tab 图标链断裂**：窗口 `WM_CLASS` 实测为程序集名 `DeepSeek.Harness.Desktop`，打包面
   desktop entry 的 `StartupWMClass`（原值 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`）匹配不上；
   且 `Window.Icon` 从未设置（`xprop` 实测 `_NET_WM_ICON: not found`，连 `WM_HINTS` 都没有）——
   Alt-Tab 图标的数据源直接缺失。用户拍板：两侧同改为 `Dotnet.Deepseek.Harness.Desktop`
   （动机：裸程序集名与官方桌面端撞名风险）。
2. **窗口几何不记忆**：全屏/改尺寸后重启回 1200x800 硬编码（`ShellWindow.cs`）。该能力
   **曾存在**：`ab2a150`（2026-10-04）接 Ryn 内建 PersistWindowState（[前件](../feature/2026-10-04-window-geometry-ryn-native-persist.md)，
   其机制已随 G5 失效并带 Erratum），真机 `window-state.json` 实证生效；
   G5 旧宿主下线（`e5ff286`）时随迁丢失，Avalonia 宿主未重建。
3. **托盘重启收尾 SIGSEGV**：coredumpctl 同栈两例（09:42:50 冷启动首绘、10:17:36 托盘重启收尾）——
   `WebKitGTK AcceleratedBackingStore::swapBuffersIfNeeded` ← `.NET 侧 gtk_main_iteration` 泵 ← #0 空指针。
   真机为 Ivy Bridge 老 GPU（MESA hasvk 不完整警告在案）。复测：三次托盘重启两崩一不崩（竞速非必然）。

## Decision

1. **窗口身份两侧同值 `Dotnet.Deepseek.Harness.Desktop`**：代码侧新增 `Hosting/X11WindowIdentity.cs`，
   OnOpened 经独立 X11 连接写 `XA_WM_CLASS`（instance=class 同值；Avalonia 12 的 X11 后端无公开
   WM_CLASS API）；打包面 `package-linux.sh` 的 `StartupWMClass` 同值并留互指注释锚。两侧覆盖面不对等：
   代码侧常量由测试 `WmClass_IsPackagingValue` 钉死到字面值（改常量即红），脚本侧无机器覆盖、漂移靠评审兜底。
   另补 `Window.Icon`（`ShellWindow` 构造注入 iconPath，装配层探测存在才传）——X11 上 Avalonia 据此写
   `_NET_WM_ICON`/`WM_HINTS`。
2. **几何持久化重建**（对齐 G5 前语义，Avalonia 原生实现）：Core 新端口 `IWindowGeometryStore` +
   `WindowGeometry` 快照（x/y/width/height/state）；Infrastructure `WindowGeometryStore` 落
   profile 状态目录 `window-state.json`（沿用 Ryn 时代文件名；JSON 源生成注册；读写 best-effort，
   损坏/缺失回缺省）。采集点=窗口态变化（即时，覆盖崩溃前最后状态）、HideToTray 与真实关窗
   （移动/缩放不逐帧写盘）；还原点=主窗构造（`WindowGeometryRestore` 纯映射：损坏尺寸跳过、
   minimized 归 normal、normal/maximized/fullscreen 还原）。
3. **真实关窗前摘 WebView**：`ShellWindow.DetachContentForShutdown()`（Content=null，幂等），
   在 `ShellApplication` 关窗判据放行真实关窗时调用——先使 GTK 泵线程无 WebKit 可画，再让 Close
   展开窗口销毁。机制定性【推断 · 未证】：「在途绘制撞销毁中的 EGL 上下文」由 SEGV 栈序推断，
   修后效果待真机多次重启观察（coredump 不再新增为通过判据）；残留 destroy 期竞速不排除，
   `WEBKIT_DISABLE_DMABUF_RENDERER=1` 留作用户侧兜底旋钮（未默认启用）。

## Alternatives considered

- 身份值保留 `io.github.ZK-Andy.dotnet-deepseek-harness-desktop`（反 DNS 结构性防撞、零生态改名）：
  用户已拍板改为 `Dotnet.Deepseek.Harness.Desktop`，被放弃。
- WM_CLASS 经改名程序集间接对齐（Avalonia 取程序集名）：牵动 NuGet 命名空间/全部引用，代价远超收益。rejected。
- 几何持久化继续依赖窗口系统会话管理（GNOME 会话恢复）：非本壳可控、对托盘重启路径无效。rejected。
- 逐帧持久化（PositionChanged 即写）：拖拽期写盘风暴，收益仅覆盖「移动后硬断电」场景。rejected。
- 收尾 SEGV 改默认 `WEBKIT_DISABLE_DMABUF_RENDERER=1`（落败）：全局改变渲染路径（所有 GPU 承担代价）
  修一台老卡；文档化为用户侧兜底旋钮保留（未默认启用），决定本身落败。
- 退出时整体 Dispose WebView（落败）：bridgeHost.Dispose 在 Closed 收口链已有固定位次，提前 Dispose
  牵动恢复页/监督器收口顺序，超出本批范围；摘 Content 已消除在途绘制面，整体 Dispose 暂缓。

## Consequences

- 启动器/Alt-Tab 图标链恢复（WM_CLASS 匹配 + `_NET_WM_ICON` 齐备）；已装机器的旧 desktop 文件
  待下次安装换新，否则匹配继续断（本来随发版落地，非额外债）。
- 冷启动恢复上次几何（含最大化/全屏）；「移动/缩放后硬断电（非关窗路径）」丢最后位置为残留代价。
- 几何落点迁移断裂：新文件 `~/.dsh/profiles/dotnet-desktop/window-state.json`，Ryn 时代旧文件
  `~/.local/share/Ryn/<appId>/window-state.json` 不读取不迁移——升级首启几何回缺省，旧文件成孤儿
  （一次性代价，用户重新摆一次窗口即重建立）。
- 托盘重启收尾崩溃面收窄（在途绘制消除）；是否绝迹待真机观察，不承诺零 coredump。
- 新文件 `X11WindowIdentity.cs`/`WindowGeometryRestore.cs` 已入 C8 在册清单；
  `WindowGeometryStore` 构造收在 `ShellCommandRegistration`（R1a 注册纪律）。
- 测试：本批净增 18 例（Infrastructure 6：存储 round-trip/损坏/缺字段/非法态/覆盖写；Headless 12：
  WM_CLASS 载荷与打包值钉、几何采集/还原/round-trip、图标装配、退出摘除、HideToTray 落盘）。
- 增量读数：gross ≈ 750 / fanout 9 / packages 0（`verify-increment-discipline.py --staged`）。gross 超 600 帽，
  取一次性豁免：三决定共享同一修复批且互为证据链（两 ADR 互链不可拆、装配面 FULL 判据要求 Review 证据随批、
  三重审核的裁定修复须同批折入），拆开会在历史里留下死链或证据断裂的中间态；豁免只对本批生效。
  增量闸豁免：gross ≤ 830，fanout ≤ 15。
