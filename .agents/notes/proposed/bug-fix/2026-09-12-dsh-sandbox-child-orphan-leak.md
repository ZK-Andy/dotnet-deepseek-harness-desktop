# Agent Note: dsh 沙箱子进程在运行时异常死亡后成为孤儿

Status: proposed

## Problem

市场一键重启（以及任何 SIGTERM/SIGKILL dsh 的路径）只终止 dsh 主进程。dsh 经 `dsh-subprocess-local` 以 systemd user scope 拉起的下游子进程（MCP 服务、bash 运行器等）不被回收，改由 `systemd --user` 收养后继续运行。

实机取证（2026-09-12）：pid 9956（codegraph MCP，`ppid=2403` = `systemd --user`）与其看门狗 9966，父 dsh 9884 于 02:03:30 被市场 SIGTERM，取证时已继续运行 51 分钟以上；其 fd 实证仍持有 `/mnt/work/dotnet-deepseek-harness-desktop/.codegraph/codegraph.db`（+`-wal`/`-shm`）。当前运行时的 codegraph 是 11523（11480 的子进程），命令行 `--path` 与 9956 相同（同一 workspace）；取证时刻 11523 的 fd 未见该 DB 句柄——**两者是否同时占用同一 DB 属【推断 · 未证】**（需在其活跃查询时复采 fd），但「一个 workspace 存在两个 codegraph 服务实例」本身是观测事实。

壳侧血统判据看不见它们的原因：dsh 的 `scrubbedParentEnv()` 会剥掉全部 `DSH_*`（含血统 token），实测 9956 的 env 无 `DSH_HOME`。这条泄漏不是本壳引入——原生 `dsh web` 下市场每次重启同样会留下它们；本壳只是更容易观察到。

## Proposal

拟议变更（未开工），三个候选方向，先定归属再动手：

1. **上游侧收敛**：dsh 收到 SIGTERM 时按 scope/cgroup 收敛自己的下游子进程（真正的归属方，覆盖所有宿主形态）。
2. **壳侧按 systemd scope 收敛**：残留 scope 名形如 `dsh-subprocess-<dsh pid>-<hash>.scope`（journal 实证），可按「已死 dsh pid」枚举并 `systemctl --user stop`；代价是依赖上游 scope 命名与 systemd（Linux-only，命名改名即静默失效）。
3. **壳侧按进程事实收敛**：对「父进程已不存在、命令行形状属 dsh 下游、起始时刻晚于某次在管运行时」的进程做有界扫描——误杀面大于方向 1/2，需先定义可证归属（例如给下游注入独立的、不受 `scrubbedParentEnv` 影响的归属标记）。

开放问题：是否先走上游（CONTRIBUTING 指定 Discussions）；壳侧兜底与上游修复的退役条件。

## Alternatives considered

- **纳入 [运行时交接收养](../../implemented/bug-fix/2026-09-12-runtime-handoff-adoption.md) 的血统扫描**：落败：`scrubbedParentEnv` 剥掉 token，扫描看不到它们；放宽成「无 token 也杀」会把用户长任务（旧的 MCP/后台作业）一并打死，违背零误杀。
- **只整树击杀收养/在管的 dsh**：落败：覆盖不到「dsh 被外力直接杀死、壳随后才察觉」的路径——此时子树已被 reparent，`entireProcessTree` 无从枚举。
- **按 systemd scope 名单收割**：可行但依赖上游 scope 命名与 systemd（记为 Proposal 方向 2，动手前需确认上游不会改名）。
- **不处理（纯记录）**：落败：现网已出现 51 分钟量级的重复 MCP 残留，且随每次市场重启累积。

## Acceptance criteria

**基线（现状可复现）**：市场重启（或 `kill -TERM <dsh pid>`）后 60s，`ps -o pid,ppid,lstart,cmd` 仍见该 dsh 的下游进程（如 `codegraph.js serve --mcp`），其 ppid 变为 1 / `systemd --user`——2026-09-12 实测 9956 存活 >50 分钟。

按方向各自的准入/准出（三者验收面不同，不共用一个阈值）：

1. **上游收敛**：准入 = dsh 的 SIGTERM 路径实现下游收敛；准出 = `kill -TERM <dsh pid>` 后 10s 内，`ps --ppid <dsh pid>` 为空，且系统内不存在该 dsh 启动过的下游进程（用重启前后 pid 差集判定）。
2. **scope 收割（壳侧）**：准入 = 能从残留 scope 名解析出已死 dsh pid；准出 = 冷启动后 `systemctl --user list-units 'dsh-subprocess-*'` 中不存在与已死 dsh 关联的 unit，且对应 pid 已消失。
3. **进程事实收敛（壳侧）**：准入 = 先定义「可证归属」并通过独立评审（判据矩阵须含「用户长任务不被误杀」反例）；准出 = 判据矩阵全绿 + 实机复采无残留下游进程。

## Risks

- 方向 2/3 都触碰「进程归属」判定，误杀用户长任务不可逆——必须先立可证归属，再谈收割。
- 上游修复时间不可控；壳侧兜底可能在上游修复后变成冗余（需写明退役条件）。
