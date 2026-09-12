# Agent Note: dsh 沙箱子进程在运行时异常死亡后成为孤儿

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

市场一键重启（以及任何 SIGTERM/SIGKILL dsh 的路径）只终止 dsh 主进程。dsh 经 `dsh-subprocess-local` 以 systemd user scope 拉起的下游子进程（MCP 服务、bash 运行器等）不被回收，改由 `systemd --user` 收养后继续运行。

实机取证（2026-09-12）：pid 9956（codegraph MCP，`ppid=2403` = `systemd --user`）与其看门狗 9966，父 dsh 9884 于 02:03:30 被市场 SIGTERM，取证时已继续运行 51 分钟以上；其 fd 实证仍持有 `/mnt/work/dotnet-deepseek-harness-desktop/.codegraph/codegraph.db`（+`-wal`/`-shm`）。当前运行时的 codegraph 是另一实例，命令行 `--path` 与 9956 相同（同一 workspace）——「一个 workspace 存在两个 codegraph 服务实例」是观测事实。

壳侧血统判据看不见它们的原因：dsh 的 `scrubbedParentEnv()` 会剥掉全部 `DSH_*`（含血统 token），实测 9956 的 env 无 `DSH_HOME`。这条泄漏不是本壳引入——原生 `dsh web` 下市场每次重启同样会留下它们；本壳只是更容易观察到。

## Decision

壳侧按 **systemd scope 收割**（原 Proposal 方向 2）落地，收割判据与零误杀边界：

- 新增 `DshSubprocessScopeReaper`：枚举 `systemctl --user list-units -- dsh-subprocess-*.scope`，解析 scope 名内嵌的 dsh 主进程 pid（journal 实证命名 `dsh-subprocess-<dsh pid>-<hash>.scope`），**owner pid 已死（`/proc` 不存在）才 `systemctl --user stop` 该 scope**；其余一律不动。
- 零误杀边界：命名形状不符（上游改名/非该形状 unit）→ 静默跳过（no-op，绝不猜）；owner pid 仍活 → 不动（覆盖另一壳实例在管的 dsh：其 pid 活，其 scope 永不被收割）；pid 复用使死者看似存活 → 同样跳过（安全方向，残留留待下次收敛，绝不把复用者当猎物）。
- stop 失败（无权限/恰好退出）留痕日志不抛——收割是增强，绝不阻断启动（对齐 `OrphanDshReaper` 的 fail-safe 风格）。
- 接线 `HarnessRuntimeHost.HarvestLineageResidue`（冷启动 + 启动成功后收敛两个时机共用该单点）：冷启动清跨 App 残留；进程内重启（市场接力后）清旧 dsh 的下游。非 Linux no-op（systemd user scope 是 Linux 形态）。
- 上游侧收敛（原方向 1：dsh SIGTERM 时按 scope/cgroup 收敛自己的下游）仍是真正的归属方，保持开放跟进——上游修复后本收割即冗余，**退役条件 = 上游实现 SIGTERM 下游收敛**；scope 命名上游改名即静默失效（no-op 方向安全）。

## Alternatives considered

- **纳入 [运行时交接收养](2026-09-12-runtime-handoff-adoption.md) 的血统扫描**：落败：`scrubbedParentEnv` 剥掉 token，扫描看不到它们；放宽成「无 token 也杀」会把用户长任务（旧的 MCP/后台作业）一并打死，违背零误杀。
- **只整树击杀收养/在管的 dsh**：落败：覆盖不到「dsh 被外力直接杀死、壳随后才察觉」的路径——此时子树已被 reparent，`entireProcessTree` 无从枚举。
- **按进程事实收敛（原方向 3）**：落败：对「父进程已不存在、命令行形状属 dsh 下游」的进程做有界扫描的误杀面显著大于 scope 判据；「可证归属」需要给下游注入不受 `scrubbedParentEnv` 影响的标记（要改上游行为），准入要求的判据矩阵（含「用户长任务不被误杀」反例）成本远超收益——scope 名单判据已够窄。
- **不处理（纯记录）**：落败：现网已出现 51 分钟量级的重复 MCP 残留，且随每次市场重启累积。
- **上游优先、壳侧等上游**：落败：上游修复时间不可控，现网泄漏随每次市场重启持续累积；scope 收割实现成本低且 fail-safe，作为兜底先行，上游修复后按退役条件退场。

## Consequences

- 收益：市场重启/SIGKILL 路径留下的下游 scope 在壳下一次冷启动或成功启动后被收敛；重复 MCP 实例持 DB 句柄的形态消除。
- 代价：收割依赖上游 scope 命名形状（`dsh-subprocess-<pid>-<hash>.scope`）——上游改名即静默失效（no-op，方向安全但功能消失）；依赖 systemd user scope（Linux-only，其他平台本来也无此泄漏形态）。
- 边界：pid 复用的窄窗内（死 dsh 的 pid 被新进程复用）收割跳过——残留留待复用者退出后的下一次收敛，不影响正确性只延迟清扫。
- 边界：本收割只覆盖「scope 内无主进程」形态；若上游未来改用非 scope 的拉起方式，判据自然失效（同命名改名）。
- 上游跟进（原方向 1）保持开放：上游若承接 SIGTERM 收敛，落地后本收割按退役条件删除。

## Testing

- 单测（`DshSubprocessScopeReaperTests`）：合法 scope 名解析出 owner pid；非法形状（前缀不符/缺 hash/缺 `.scope`/pid 溢出 int）解析失败即跳过；owner 死 → stop 且计数；owner 活 → 不动；stop 抛异常 → 留痕继续、不计入成功数；空 unit 列表 → 0。
- 实机验证（2026-09-13）：`systemd-run --user --scope --unit=dsh-subprocess-999999-abcdef.scope /usr/bin/sleep 120` 造一个 owner pid 已死的假残留 scope（999999 不存在）→ dev 实例冷启动 → host.log 记「`收割残留下游 scope：dsh-subprocess-999999-abcdef.scope（owner dsh pid 999999 已死）`」→ `systemctl --user list-units` 中该 unit 消失；对照：同轮收敛中在管 dsh（pid 61299 存活）的 `dsh-subprocess-61299-*.scope` 未被触碰。

## Related

- [runtime-handoff-adoption](2026-09-12-runtime-handoff-adoption.md)：血统扫描/收养是「带 token 可见」的残留面；本篇是「token 被剥不可见」的补集。
- [self-update-exit-reaps-dsh-child](2026-08-28-self-update-exit-reaps-dsh-child.md)：`OrphanDshReaper` 的 token 复验清扫；本收割与其同接 `HarvestLineageResidue` 收敛点。
