# Agent Note: shell-convenience-autostart-ready-notify

Status: implemented

## Problem

批次三五项中的三项属纯壳层能力、可离线完成：①自更新已是「检查→静默下载→ready」的自动化链路，但 ready 到达时用户无感知（侧栏按钮出现即全部告知），违背「完成后提示一键安装」的体验目标；②安装失败的状态机回退（Installing→Ready、资产保留、错误帧上页面）已实现但无回归测试钉住，属未固化的行为契约；③开机自启缺失——轻壳层标配，且共享 home 后桌面常驻价值上升。

系统托盘与关闭最小化两项**本轮不做**：其前置功课（Linux AppIndicator/StatusNotifier 行为实测、Ryn 关窗事件拦截面验证）需真实桌面环境，沙箱无法完成实测，强行实现等于在未验证平台上盲写代码。

## Decision

1. **就绪横幅**：`onTransition` 进入 Ready 且带版本号时（每运行周期一次，去重）注入顶部横幅「新版本 X 已就绪…」；脚本构建器纯函数化入 `Services/Update/UpdateBanner`。
2. **失败回退固化**：新增回归测试断言 install 委托抛出后状态机回到 Ready、持久化记录与资产文件原样保留、错误消息经路由帧到达页面——把既有行为升级为被测试钉住的契约；不改动状态机逻辑。
3. **开机自启**：`Services/Autostart` 三平台实现——Linux 写 `~/.config/autostart/*.desktop`（XDG 标准）、Windows 写 HKCU Run 键、macOS 写 LaunchAgents plist；可执行路径取 `Environment.ProcessPath`。开关状态查询/切换走 `desktop.autostart.getState/set` 命令路由，companion 设置页新增「桌面」区块承载开关（version bump）。条目文本构造器纯函数化可单测；Windows 注册表分支不在沙箱测试矩阵内，靠类型系统约束最小面。

## Alternatives considered

- **托盘与本批同轮实现**：落败——AppIndicator 行为、图标材质、关窗拦截均需实机验证；未实测平台的托盘代码历史上正是「🟡 等社区踩坑」的来源，违背 fail loud 与最小证据纪律。
- **就绪提示用系统通知（notification）而非应用内横幅**：落败——Ryn Alpha 的 notification 能力未验证（在案风险）；横幅复用既有注入通道零新面。托盘到位后可再加系统通知选项。
- **自启开关存 appsettings.json**：落败——该文件随安装目录分发（只读、机器级），而自启是用户级偏好；落 home 侧与诊断白名单同理需防泄漏（自启条目非敏感，仍按白名单纪律不加进诊断包）。
- **失败回退做自动重试**：落败——安装失败多因授权取消/包损坏，自动重试只会循环弹窗；保留 ready 态由用户手动重试是既有拍板语义。

## Consequences

- 就绪横幅经状态机订阅建立（窗口句柄就绪后），每运行周期去重一次；与既有横幅共享注入通道与堆叠规则。
- 失败回退从「隐式行为」升级为「被回归测试钉住的契约」：install 抛出 → ready 恢复 + 资产保留 + 错误帧可见。
- XDG autostart 仅覆盖支持该规范的会话（主流 GNOME/KDE/plasma）；极简 WM 可能不生效——属已知边界。plist 关键字段（Label/ProgramArguments/RunAtLoad）由单测锁定。
- 自启条目指向 `Environment.ProcessPath`（自更新原地升级不改路径，无需跟踪）；若未来改版本化目录则此项必须跟随。
- 托盘与关闭最小化两项未在本轮（前置实测依赖真实桌面环境），台账保持未勾。

## Related

- [dev 运行时隔离](../process/2026-08-22-dev-runtime-isolation.md)：dev 实例的自启开关写在其隔离 home 对应的用户目录，与正式版互不干扰。
- [companion 更新设置页](../feature/2026-08-22-companion-update-settings-section.md)：「桌面」区块（order 52）挂同一 settings.section 机制之下。
