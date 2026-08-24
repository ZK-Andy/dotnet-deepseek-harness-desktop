# Agent Note: shell-tray-hide-to-tray

Status: implemented

## Problem

批次三剩余两项：①系统托盘（图标 + 菜单：显示主窗/检查更新/退出）——常驻壳层的标配入口，共享 home 后桌面端数据价值上升，用户需要不依赖任务栏的召回与退出通道；②关闭最小化到托盘——点 X 默认隐藏、托盘可召回，托盘菜单才真正退出。前置功课已齐：`Ryn.Plugins.Tray 0.30.1` API 面实证（`IconPath`/`SetMenu`/`Show`）、`IRynWindow.Closing` 可取消关窗实证（`WindowClosingEventArgs.Cancel → SAUCER_POLICY_BLOCK`）、上游源码确认插件初始化按 DI 注册顺序执行。

## Decision

1. **托盘注册**：`AddRynTray` 进 `ConfigureServices`；Build 后解析 `TrayService` 装菜单并 `Show()`。菜单项 = 显示主窗 / 检查更新（仅自更新栈装载时）/ 分隔线 / 退出。
2. **点击语义走 Web 层中继（关键取舍）**：托盘点击事件在上游被 `TrayService.EmitEvent`（internal 属性）发往 Web 层（`window.__ryn._emit`）。原生侧拦截需反射私有面——本项目 `PublishAot=true`，反射跨程序集私有成员在 AOT 下不可靠，**排除**。改为：companion 插件做哑中继（`__ryn.on('tray.clicked'/'tray.menuItemClicked')` → `invoke('desktop.tray.event', {event,data})`），宿主新增同名命令路由把事件解析为动作。动作解析留在宿主纯函数（`TrayMenuActions.TryResolve`）可单测；中继只转发白名单事件名，不含语义。
3. **hide-to-tray**：订阅 `IRynWindow.Closing`（deferred 代理会缓冲订阅）；默认 `Cancel=true` + `HideAsync()`。放行唯一通道是 `CloseGate.ApproveExit()`——托盘「退出」与自更新安装路径（原有关窗让位逻辑）先批准再 Close，两处共用同一闸门防语义漂移。
4. **失败降级耦合**：托盘初始化失败（无系统托盘环境等）只记日志降级，但 **Closing 拦截必须同 gate**——没有托盘还拦截关窗等于把窗口藏死。tray 未就绪时关窗行为保持原生直退。

## Alternatives considered

- **反射接管 `EmitEvent` 实现纯原生菜单响应**：落败——internal 属性需 NonPublic 反射且后写者胜的赋值语义要求精确的初始化时序；NativeAOT 裁剪下跨程序集私有反射不可用（fail loud 纪律也不允许静默跳过）。
- **菜单动作全部由 companion 前端自行处理（不经宿主）**：落败——显示/关闭窗口是宿主能力，前端无从触达；且语义分散两处违背「行为级变更配套回归测试」的可测性要求。
- **自更新安装路径复用托盘退出命令**：落败——安装路径已有 8s Environment.Exit 兜底与独立的授权语义，强行合并会把两条失败域拧在一起；只共用 CloseGate 一个原子事实。
- **托盘初始化失败即启动失败（fail loud 到进程级）**：落败——无托盘环境（部分平铺 WM/远程桌面）是合法运行环境，进程级失败把「少一个增强入口」升级成「完全不可用」；降级为关窗直退并留日志已把风险显式化。

## Consequences

- 托盘菜单点击依赖页面存活（中继链路）：dsh 重启恢复期与首屏加载完成前的点击会被丢弃；隐藏态页面仍存活故 hide-to-tray 主场景不受影响。Linux 上游声明 tray 为 menu-only（无图标单击事件）——「显示主窗」在 Linux 经菜单触发。
- 关闭窗口从「退出应用」变为「隐藏到托盘」，属用户可见行为变更：user-guide 双语与 FAQ 同步改写；真正退出的路径 = 托盘菜单「退出」。
- companion 中继代码入包必须 version bump（0.0.6→0.0.7），否则已装用户升级后静默无中继。
- **实机验收清单（发版前必须逐项过）**：①Linux AppIndicator 图标可见（GNOME 无 AppIndicator 扩展的环境托盘不可见，属已知风险）；②X 隐藏 → 托盘菜单「显示主窗」召回；③X 隐藏后从启动器再次拉起的行为记录——GTK 单实例互斥下 saucer 是否 activate-present 未验证，Ryn 不暴露二启拦截面，若实测不能召回则立项「第二实例→ShowMainWindow 接线」（需上游扩展点或平台原生 hack）；④Windows/macOS 图标点击显示主窗。搁浅兜底：极端搁浅只能 kill 进程，RunMarker 会在下次启动给出非受控退出横幅留痕。
- 退出路径的顺序契约（先 ApproveExit 再 Close）由记序 fake 回归测试钉住；返回帧 "{}"/"null" 双态沿用 openExternal 约定并在路由 XML doc 注明。

## Related

- [批次三前三项](2026-08-24-shell-convenience-autostart-ready-notify.md)：同一台账批次；该轮以「需真实桌面实测」为由挂账本两项。
- [companion 更新设置页](../feature/2026-08-22-companion-update-settings-section.md)：「检查更新」菜单项复用其状态机与设置页状态帧链路。
