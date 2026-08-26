# Agent Note: child-process-reaping-port-drift

Status: implemented

## Problem

实机取证（v0.3.7，host.log + 端口记忆文件轨迹 46777→42859→39157→44037）坐实冷启动会话选中态丢失链：壳退出后 dsh 子进程未被确定性回收 → 孤儿占住首选端口 → 新实例 `--port <preferred>` 绑定失败回退 OS 分配并回写端口文件 → WebView origin 变化 → 按 origin 隔离的「当前会话」localStorage 失配。正常退出路径（`app.Run()` 返回后 `supervisorCts.Cancel()` → `host.Stop()` 整树击杀 → marker Release）本身完整，滞留风险在 **GTK loop 对隐藏态窗口的 close 可能不退出主循环**——托盘退出恰发生在 hide-to-tray 之后，`Close()` 已调用（「已放行关窗」有日志）但主循环未返回时，`Stop()` 永不到达且全程无痕。

## Decision

1. **托盘退出升级为确定性有序退出编排**：退出路由不再裸调 `trayWindow.Close()`，改走编排委托——`ApproveExit` → 取消监督器令牌 → `host.Stop()`（整树击杀 dsh，先于关窗执行，运行时回收不再依赖 GTK loop 行为）→ marker Release → `Close()` → **8s 退出看门狗**（主循环届时仍未返回则记日志并 `Environment.Exit(0)`，把静默滞留变成确定性终结）。编排委托经持有器延迟接线（注册期早于 supervisorCts 声明），恢复页退出路由维持原序不扩批。
2. **端口漂移显式告警**：`HarnessRuntimeHost.StartAsync` 首选端口绑定失败回退 OS 分配成功时，写 host.log warning——点明疑似残留实例/孤儿占用、本次 origin 将变化、上一会话选中态不保留。运行时宿主新增可选日志依赖（缺省 null 安全）。
3. **自更新 `Environment.Exit(0)` 兜底路径维持现状**：该路径由 pkexec 脚本接管进程接力，强退前补 Stop 的收益与脚本时序耦合，留待实机复现孤儿后再议。

## Alternatives considered

- **启动期孤儿检测与端口复用（attach 存活 dsh）**：落败——需要 HTTP 探活判定归属、处理多写者竞争，复杂度远超收益；单实例仲裁（姊妹决策）消解多实例叠加后，孤儿只剩崩溃残留一种来源，漂移告警已给出人可判读的信号。
- **dsh 侧 PDEATHSIG / setsid 进程组方案**：落败——Linux-only 且要动 spawn 形态；`Kill(entireProcessTree: true)` 已覆盖树击杀，缺口只在「是否被调到」，修编排而非修击杀手段。
- **Close 之后才 Stop（保持原序加看门狗）**：落败——滞留场景下 Close 本身不可靠，把唯一确定的回收动作押在它后面等于没修；先 Stop 后 Close 让资源回收与 loop 行为彻底解耦。

## Consequences

- 托盘退出在隐藏态/可见态都确定性终结进程；dsh 不再有「壳已退、子还活」的窗口期，冷启动首选端口命中率高，会话选中态随之稳定。
- 看门狗 8s 是经验值：覆盖 supervisorTask.Wait(2s) 与 GTK 清理的正常余量；误触发代价仅为提前 Exit，收尾动作已在 Exit 前完成，无双重释放面。
- 端口告警是观测位不是修复位：出现即说明仍有残留来源（崩溃/外力 kill），按日志指引人工处置或后续立项 attach 方案。

## Related

- [单实例 launcher 激活](../architecture/2026-08-26-single-instance-launcher-activation.md)：同批姊妹决策——多实例诱因的根治面。
- [端口记忆按 profile 隔离](2026-08-26-port-memory-per-profile.md)：首选端口持久化机制的出处。
- [托盘与关闭最小化](../architecture/2026-08-24-shell-tray-hide-to-tray.md)：ApproveExit→Close 顺序契约与本编排放大后的关系。
