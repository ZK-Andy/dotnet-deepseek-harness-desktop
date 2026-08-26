# Agent Note: single-instance-launcher-activation

Status: implemented

## Problem

实机（Fedora 44 / GNOME Wayland，v0.3.7 自更新后）坐实两个症状：①应用已在后台（托盘）时从桌面图标再次点击，**窗口唤不起来**——每次点击都产生完整的新实例启动序列（host.log 四次「系统托盘已注册」相隔数秒）；②多实例快速交替使 run-marker token 互相覆盖，「上轮未正常退出的标记」误报连发。根因：GTK/saucer 按 ApplicationId 的单实例互斥在 Wayland 会话不生效，而 Ryn 不暴露二启激活拦截面（历史结论），新实例自行拉起却无人负责显示已有窗口。

## Decision

1. **壳内自建单实例仲裁，不依赖 GTK 互斥**：新增 `Services/LauncherActivation`——首实例对 Unix domain socket `bind` 成功即为主实例并进入 accept 监听；`bind` 撞地址（EADDRINUSE）即为二次启动，向既有 socket 发送一行 `show` 命令、收到 `ok` 应答（2s 超时）后以 0 退出，无论通知成败都绝不重复拉起运行时。
2. **主实例收到 `show` 即显示主窗**：回调经 `CurrentWindowAccessor` 的 deferred 代理走 `ShowAsync`（与托盘「显示主窗」同构，异常吞掉记日志——激活是增强能力，绝不拖垮任一实例）。
3. **锁地址随 dev 隔离**：socket 路径取 `$XDG_RUNTIME_DIR`（缺失回退 `/tmp`）+ 应用名 + `.dev` 后缀规则与 `DevEnvironment.ApplicationIdFor` 同源，开发实例与正式版互不顶牛；主实例退出时 unlink socket。
4. **平台边界**：Linux/macOS 启用（UDS 两平台皆原生）；Windows 本轮不启用（无验证环境，行为维持现状），代码显式分支并记录——与「Win/mac 无实证不盲扩」的项目纪律一致。

## Alternatives considered

- **依赖 GTK/saucer 单实例互斥并给 activate 接 show**：落败——Wayland 实证互斥未拦截二启（本批取证），且 Ryn 不暴露 activate-present 拦截面，等上游不可控；自建仲裁在进程最早期执行，先于任何 GTK 初始化，行为确定。
- **DBus 名称持有（org.freedesktop.ApplicationKit 式）**：落败——引入 DBus 依赖只为一个互斥原语，UDS 三平台语义一致且沙箱可测。
- **锁文件 + flock**：落败——flock 只能判定「有人」，无法传递「show 主窗」消息；二启唤醒恰恰是本次的核心需求。
- **Windows 一并实现 named pipe 版本**：落败——无真机验证手段（社区支持档），盲扩回归面大于收益；分支显式留白待证据。

## Consequences

- 二次启动不再产生第二份运行时/dsh 子进程/托盘图标；端口漂移与 marker 误报的多实例诱因随之消解（孤儿残留另见 child-process-reaping 笔记）。
- 主实例崩溃遗留 socket 文件时：`bind` 对残留文件会 EADDRINUSE——connect 探活失败（对端不存在）则删除重建（自愈路径有集成测试钉住）。
- 激活显示不携带最大化保持（样本属本实例会话态）——首唤几何问题归 tray-recall-maximize 线，不在本批范围。

## Related

- [子进程收割与端口漂移](../bug-fix/2026-08-26-child-process-reaping-port-drift.md)：同批姊妹决策——多实例消解后仍存的孤儿残留与漂移告警。
- [共享 home + desktop profile](2026-08-23-shared-home-desktop-profile.md)：DSH_HOME 解析与 dev 隔离的出处。
