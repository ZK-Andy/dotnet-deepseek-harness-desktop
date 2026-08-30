# Agent Note: 自更新退出路径确定性收割 dsh 子进程

Status: implemented

## Problem

v0.3.11 实机复现：自更新 0.3.6→0.3.11 后，宿主退出时其 dsh 子进程未被收割成孤儿，连续占住首选端口导致跨实例漂移链。host.log + install.log + `ss`/`ps` 进程快照三重实证：

```
[旧 0.3.10 宿主] 在 36221 正常跑 → 用户触发自更新
→ install.sh（pkexec）等宿主 pid 退出 → rpm -U 装 0.3.11 → 降权重启
→ 旧宿主退出时其 dsh 子进程 `bin.js --port 36221`（pid 70587）未被整树击杀 → 孤儿占 36221
→ 新 0.3.11 实例#1：首选 36221 被占 → 漂移 37941（dsh 71537 成新孤儿）
→ 实例#1 宿主退出 → 71537 也未被收割 → 孤儿占 37941
→ 新 0.3.11 实例#2（当前）：37941 被占 → 漂移 41331
```

`ss` 实证 36221（pid 70587）与 37941（pid 71537）皆 `LISTEN`；`ps -o ppid` 实证两者 **PPID = 2458（`/usr/lib/systemd/systemd --user`）**——宿主进程已死、dsh 被 systemd --user 收养，是确凿的孤儿残留。

这命中姊妹决策 `child-process-reaping-port-drift` 第 3 点**明确留白**的自更新路径：

> 「自更新 `Environment.Exit(0)` 兜底路径维持现状：该路径由 pkexec 脚本接管进程接力，强退前补 Stop 的收益与脚本时序耦合，留待实机复现孤儿后再议。」

本次复现，收口该留白。**两个缺口**：

- **缺口 A（自更新/优雅退出）**：自更新 `install` 委托授权通过后只 `Close()` 关窗 + `StartExitFallback(ct)`（8s 后 `Environment.Exit(0)`），**从不调用 `host.Stop()`（整树击杀 dsh）**。若 GTK 主循环在 8s 内未返回（ADR 已证隐藏态 close 可滞留），`Environment.Exit(0)` 绕过 Main 尾部 `host.Stop()`，dsh 成孤儿。
- **缺口 B（宿主异常死亡）**：宿主被 SIGKILL/崩溃/非走退出编排时，进程内无代码能执行 `host.Stop()`。缺口 A 只覆盖优雅退出；异常死亡残留由**冷启动清扫**兜底（下见 Decision）。

## Decision

**两段修复，覆盖全部退出形态。**

### 缺口 A：自更新兜底强退前先确定性收割

把自更新退出路径的「兜底强退」从裸 `Environment.Exit(0)` 升级为「先收割 dsh，再强退」，与托盘有序退出编排对齐。关键改动（DesktopBootstrap/DesktopBootstrap.Startup）：

1. **新增 `Action? updateExitReaper` 持有器**（顶部声明，与 251 行 `orderlyQuit` 同为延迟接线持有器）。`supervisorCts`（446 行）在 install 委托（195 行）声明点之后，C# 闭包无法捕获（CS0841，见 Alternatives），故经此持有器延迟接线；`StartExitFallback`（847 行，晚于全部依赖）在触发时调用它。

2. **`updateExitReaper` 在 `supervisorCts` 声明后接线为回收三件套**：`supervisorCts.Cancel()`（停监督器，防恢复分支 spawn-after-cancel 竞态）+ `host.Stop()`（整树击杀 dsh）+ `RunMarker.Release(home, marker.Token)`。与 `orderlyQuit` 同款「先回收再退」。

3. **`StartExitFallback` 改为先回收再强退**：
   ```
   Task.Run(async () => {
       await Task.Delay(8s, ct);
       HostLog.Write("[update] 退出兜底触发：回收 dsh 后强退");
       updateExitReaper();             // 已接线则收割三件套
       // 未接线（异常时序）则回退直杀 dsh，仍不泄漏
       Environment.Exit(0);
   })
   ```

4. **`install` 委托保持 `Close()` + `StartExitFallback(ct)`**：回收集中在兜底单一职责，不重复。正常时 `app.Run()` 返回 → Main 尾部 `host.Stop()` 先到；滞留时兜底 `StartExitFallback` 的 `host.Stop()` 接管。**两个收敛点，`Stop()` 幂等无双重收割面**（与 ADR `child-process-reaping-port-drift` 的「双路径收敛」同款）。

### 缺口 B：冷启动清扫孤儿 dsh（token 复验，零误杀）

新增 `Services.OrphanDshReaper`：宿主**异常死亡**后，其 dsh 成 systemd 收养孤儿占端口——下次冷启动清扫。

**核心「零误杀」设计**：绝不裸用 PID 匹配（PID 复用指向无关进程会误杀，不可逆）。改为——

1. **spawn 时注入唯一 token**：`HarnessRuntimeHost.StartCoreAsync` spawn dsh 时设环境变量 `DSH_DESKTOP_SPAWN_TOKEN=<uuid>`（Guid.NewGuid），并把 `pid\ntoken` 写入 profile 的 `.dsh-pid` 文件（`PersistSpawn`）。
2. **冷启动清扫**：`HarnessRuntimeHost.StartAsync` 开头（仅 `_port is null` 的冷启动时）调 `OrphanDshReaper.Reap`：读 `.dsh-pid`,取 `(pid, token)`;用 `ReadTokenLinux()`（读 `/proc/<pid>/environ` 找 `DSH_DESKTOP_SPAWN_TOKEN`）复验该 PID 进程环境带的 token 是否与记录一致。**一致才整树杀**；`readToken` 读不到（进程已死）/不一致（PID 复用）/非 Linux——一律**不杀**，只记日志，端口漂移告警兜底。

跨平台可测：`OrphanDshReaper.Reap` 接受注入的 `readToken(pid)` 与 `killTree(pid)` 委托，纯逻辑可 xunit 单测（见 Testing）。生产封装 `ReadTokenLinux()` 与 `KillTreeProcessTree()`（`Process.Kill(entireProcessTree)`）。

## Alternatives considered

- **提前 `supervisorCts` 声明让 `install` 委托直接引用**：落败——CLR 要求局部变量在**声明点之后**才能被闭包捕获（`CS0841`，实机编译验证），提前声明破坏 250 行「注册期早于 supervisorCts/marker 声明，故经持有器延迟接线」的有意设计，且触及面远大于收益。
- **`install` 委托里直接 `host.Stop()`**：部分可行——`host`（146 行）在委托之前可引用，但 `supervisorCts` 引用不到，漏掉监督器 cancel 留「reaper 杀 dsh 后监督器又 spawn」竞态；回收分散在 `install` + 兜底两处，职责不单一。
- **复用 `orderlyQuit` 做自更新退出**：落败——`orderlyQuit` 晚于 `install` 委托声明点，闭包无法引用（同 CS0841）；且绑定托盘窗口 Close 语义，携 holder 延迟接线现状非本批目标。
- **缺口 B 走 PDEATHSIG（prctl SET_PDEATHSIG）**：落败——Linux-only 且要动 spawn 形态，跨平台不可测；姊妹决策 `child-process-reaping-port-drift` 已拒（Linux-only + 动 spawn 形态）。token 清扫跨平台、零误杀，更稳。
- **缺口 B 裸 PID 匹配清扫**：落败——PID 复用指向无关进程时误杀不可逆（比端口漂移代价严重）；必须 token 复验才杀。
- **缺口 B attach 存活 dsh（端口探活归属）**：落败——需 HTTP 探活判定归属、处理多写者竞争；token 清扫更简单且零误杀（姊妹 ADR 已拒此路线，此处不重议）。

## Consequences

- 自更新/优雅退出在任何情况下（`app.Run()` 及时返回或 GTK 滞留）都确定性收割 dsh 子进程树；异常死亡残留由下次冷启动 token 复验清扫兜底。不再有「壳已退、dsh 还活」的窗口期，冷启动首选端口命中率回升，会话选中态随之稳定。
- 兜底 `StartExitFallback` 的 `supervisorCts.Cancel()` 为**新增**监督器取消点：与 `orderlyQuit` 取消双检查一致，封住恢复分支 spawn-after-cancel 竞态。
- 崩溃恢复路径（dsh 已死再 `RestartAsync`）的 `Stop()` 跳过 `HasExited` 进程属**正确**行为（已死无需 kill）；`RestartAsync` 的 `_port` 非 null，不触发冷启动清扫，无重复。
- 端口漂移**告警**维持观测位不变：token 清扫只管能复验的；仍有来源不明残留（跨 profile 争抢、外力 kill 后 token 已随 /proc 不可读）时，漂移日志仍是人可判读信号。
- 成本：新增 `.dsh-pid` 文件（persist 尽力而为）+ 每次冷启动一次 `/proc/<pid>/environ` 读（约零开销，仅单个 PID）。零误杀以「token 复验不通过即不杀」为代价，极少数场景（进程环境含正确 token 但确非本 dsh）理论上不会发生——token 是唯一 GUID 且随 spawn 注入。

## Testing

- 新增 `OrphanDshReaperTests`（6 用例）：token 匹配→杀；token 不匹配→不杀；token 读不到→不杀；PID 文件缺失→不杀；记录损坏→不杀；杀失败→返回 false 不抛。全部实现「零误杀」决策契约。
- `StartExitFallback`/`updateExitReaper` 为 DesktopBootstrap 局部编排（同 `orderlyQuit` 先例，不单测），其正确性由「回收序列 = orderlyQuit 同款三件套」单一职责保证 + 门禁把关。
- 行为级验证依赖**实机复测**：自更新 0.3.11→下一版后核对 `ss` 无孤儿（36221/37941 此类残留不再出现）、host.log 无「首选端口被占」漂移告警。

## Related

- [子进程收割与端口漂移](2026-08-26-child-process-reaping-port-drift.md)：本决策收口其第 3 点自更新留白；有序退出编排的姊妹决策。
- [端口记忆按 profile 隔离](2026-08-26-port-memory-per-profile.md)：首启端口持久化与漂移兜底的出处。
- [GUI 冻结取证探针](../process/2026-08-28-gui-freeze-forensics-probe.md)：本次事故的现场来自探针脚本（`probe-gui-freeze.sh`）与 host.log/install.log 交叉核对。
- [split-program-main-god-function](../architecture/2026-08-30-split-program-main-god-function.md)：本 ADR 的局部编排随 P0 拆 Main 迁至 `DesktopBootstrap`。
