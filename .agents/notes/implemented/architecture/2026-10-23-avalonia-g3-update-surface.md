# Agent Note: Avalonia G3 更新面——三条命令、就绪横幅与托盘检查的页内反馈

Status: implemented

Review: FULL/2026-10-24#1/R1=ok R2=ok R3=ok

## Problem

G3 的目标状态是命令面十二条路由齐（[avalonia-host-migration](../../proposed/architecture/2026-10-22-avalonia-host-migration.md) 阶段表）。
前几片接完自启/关窗到托盘/诊断导出/重启、页面桥四路由与恢复面后，新宿主仍缺自更新三条路由
（`desktop.update.getState` / `check` / `install`）：伴生插件的侧栏更新钮与「桌面设置」更新块在新宿主下
invoke 即失败，页面侧只能报「命令通道断」。

同一缺口还有两处表示面：旧宿主的 ready 就绪横幅由 `PagePump` 注入，新宿主没有注入点；托盘菜单缺
「检查更新」，而旧宿主的实现依赖 `TrayService` 的**系统通知**把结论送达——Avalonia `TrayIcon` 没有通知面，
菜单触发后用户拿不到任何反馈（[avalonia-g3-exit-restart](2026-10-23-avalonia-g3-exit-restart.md) 的
Consequences 与 `Hosting/ShellTray.cs` 的类注释都记着这个缺口，且明确「随更新片定策」）。

## Decision

- **更新栈在注册处构造**：`Update/UpdateSurfaceRegistration.cs` 一次成交（`UpdateEnvironmentAdapter` /
  `ReleaseFeedAdapter` / `PackageDownloaderAdapter` / `UpdateInstaller` 四适配器 + `UpdateCoordinator.Load`），
  与旧宿主 `Bootstrap/PreBuildRegistration` 的自建路径并存——旧路径随 G5 下线，期间两宿主各自持有实例，
  不共享状态机（无容器，跨宿主共享会让 dev 门禁与寿命面互相牵连）。
- **三条路由按装载与否给值**：`coordinator.Machine` 为 null（dev 门禁且未设 `DSH_DESKTOP_UPDATE_FORCE`）时
  路由表为空、托盘动作为 null——与旧宿主同判据「栈未装载即不出现」，invoke 自然失败，不注册假路由。
- **启动检查与就绪横幅**：`UpdateSurface.Start()` 在窗口接线期（托盘创建之后、窗口 Opened 之前）调用
  `coordinator.Start()`——启动对账 + 后台检查一次，并订阅 ready 就绪横幅。横幅落点有 holder 判据：
  `PagePump` 先探当前文档是否仍是 holder 过渡页（`WebAuthRecovery.HolderMarker`），是则按节拍重试到真 UI
  出现再注入——否则横幅会随 holder 的 `location.reload()` 一起消失，而协调器的 `_readyNotified` 已置位、
  本轮不再补发。状态变化仍以 `dsh-desktop-update` 事件推页面
  （`PagePump.PushUpdateState`），使设置页状态行与侧栏钮跟托盘结论一致。
- **托盘菜单内容复用同一模型**：`ShellTray` 不再自拼菜单，改用 `TrayMenuActions.BuildEntries`（位次/文案的单一
  家：显示主窗 / 检查更新 / 分隔线 / 重启 / 退出），本类只做「条目 id → 原生回调」映射；未知 id 抛错——
  两处漂移属装配错误，不静默吞。`TrayMenuActions` 因此从「旧宿主菜单事实源」变为两宿主共用，随 G5 一起搬去共享家。
- **反馈面 = 唤回主窗 + 页内横幅（本片的核心决定）**：托盘触发的检查结论先 `ShellRecall.Request()` 唤回主窗，
  再注入横幅：
  - `ready` 复用就绪横幅（`UpdateBanner.ReadyScript`，id `dsh-desktop-update-ready-banner`）——与协调器自身的
    ready 订阅同 id，先到者胜，不会出现两张同义横幅；
  - `up-to-date` / `error` 落检查横幅（id `dsh-desktop-update-check-banner`，登记进 `DesktopBanner.KnownIds`
    参与堆叠计数），`DesktopBanner.Build` 增 `replaceExisting` 形参：用户主动动作的结论在同名横幅已存在时
    先移除再注入，连续两次结论不同是刷新而不是静默丢弃；
  - 中间态（checking/downloading/installing/idle）不唤回、不注入，只留 host.log；
  - **跨横幅互斥**：两种结论各自先清掉对方那张（`DesktopBanner.ClearScript`）——就绪清检查横幅，
    已是最新清就绪横幅；失败**不**清就绪横幅（网络抖动不该抹掉可操作的安装提示）。
- **反馈面不碰恢复页/失败面**：恢复页承载的是 dsh 崩溃与引导失败，把「已是最新」塞进去会让恢复面的语义
  从「出事了」稀释成「通知中心」；失败面是进程内模态，用于检查失败过重。均落败（见 Alternatives）。

## Alternatives considered

- **补原生通知（旧宿主形态）**：Windows toast 需注册 AUMID/快捷方式，未登记即静默不弹；Linux 走
  `org.freedesktop.Notifications`，无守护进程时退化成另一种「没反馈」；macOS 需 bundle identity 与用户授权，
  开发运行态基本静默。三平台各带一条静默失败面，用它填「没有反馈面」的洞是把不确定性搬进来。落败。
- **菜单项状态化（点完把 Header 改成「检查中…/已是最新」再复位）**：Avalonia 12.1.4 的
  `NativeMenuItem.Header` 是可写属性（已核 ref 程序集），但改 Header 后 Linux DBusMenu 是否即时重绘**未验证**；
  且失败原因放不下（只能短句）。落败——保留为将来补足项。
- **只写 host.log**（`ShellTray` 之前的现状）：用户拿不到反馈，等于没做。落败。
- **把结论塞进恢复页/启动失败面**：见 Decision 末条，语义稀释 + 模态过重。落败。
- **两宿主共享一个更新栈实例**（把旧宿主构造挪进新注册文件复用）：新宿主无容器，共享需要给旧宿主留注入面，
  且 dev 门禁与监督器令牌源寿命不同源；G5 删除旧宿主后这层共享就是纯负担。落败。

## Consequences

- 托盘检查会把窗口从托盘拽出来（夺焦）——这是「用页内面替代系统通知」的直接代价；用户点的是主动动作，
  且结论必须可见，接受。
- ready 首次由托盘检查发现时，协调器的就绪横幅与本反馈同 id，只出现一张；若该轮已弹过就绪横幅，再次检查
  到 ready 只唤回窗口、不重复弹（页面设置行本就有持久呈现）。
- 检查横幅的 `replaceExisting` 是 `DesktopBanner` 契约的扩展（缺省 false，既有三条横幅行为不变）。
- 语言切换后托盘菜单仍不重建（既有缺口，不在本片）。`TrayCheckFeedback` 的结论文案与 `TrayMenuActions` 的菜单模型
  现由两宿主共用，随 G5 搬去共享家；留到 G5 删除的只有旧宿主的通知路径（`DesktopTrayCommandRouter` 的 `_notify` 与其
  「检查更新」分支），删之前先摘掉这两处对新宿主的误导性指针。
- 未覆盖面（同类失效，转下批）：版本底线与非受控退出横幅仍走无落点判据的 `ShowBannerWhenReadyAsync`，
  同属「注入到 holder 会被 reload 吞掉」的形状，本片只收口更新面两条。
- 真机验收仍缺：托盘「检查更新」点击、横幅在隐藏-唤回后的可见性需要桌面环境（已挂 HANDOFF 待办「托盘真机验收」的检查项）。
- 增量读数：gross 1035 / fanout 10 / packages 0，见本批 `verify-increment-discipline.py --staged`（评审两轮的修复同批折入）。
  gross 超 600 帽，取一次性豁免：本片是一条完整反馈链（三条路由 → 协调器 → 托盘项 → 结论横幅），拆开会在历史里
  留下「按钮可点、结论落空」的中间态，而 ADR 与测试按仓库硬规则必须同批；豁免只对本批生效。
  增量闸豁免：gross ≤ 1050，fanout ≤ 15，packages ≤ 1。

## Testing

- `UpdateCheckPresenterTests`（8 方法 / 11 用例，含 4 行 InlineData）：已是最新/失败/就绪三态分别落对横幅与 id、
  失败带原因与警示色、中间态不打扰、同名替换与跨横幅互斥清屏、后台检查路径交付结论、取消短路不唤回、
  令牌源已释放时收口不上抛。
- `ShellTrayActionTests`（3 例）：四条菜单 id 互不串位、未知 id 抛错、每项 Header 逐位等于共用菜单模型的文案。
- `UpdateSurfaceRegistrationTests`（2 例）：机器为空/装载两分支——路由与托盘动作同进退，装载时三条命令各有路由。
- `PagePumpTests` 增 2 例：holder 文档上不注入、离开 holder 后补注入；始终 holder 时按预算放弃。
- 全量（本机）：1203/1203（壳 347 / Core 234 / Infrastructure 616 / Analyzers 6），`dotnet build` 0 警告。
  机器家（`scripts/test-baseline.json` + README 徽章）仍指 1185，覆盖率与测试数跟值挂 HANDOFF 待办。
