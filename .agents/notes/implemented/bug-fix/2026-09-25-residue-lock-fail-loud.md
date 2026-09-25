# Agent Note: residue-lock-fail-loud（残留占位预检：杀不掉即 fail loud）

Status: implemented

Review: FULL/2026-09-25#3/R1=ok R2=ok R3=ok

## Problem

竞品残留进程锁死 `dependencies\dsh`，`os error 5` 且 UI 无提示。我方缺口：收割（`OrphanDshReaper` token 复验 + scope 收割 + 血统扫描）仅 Linux 真机验证——Windows/macOS 上 `ReadToken` 恒 null（无 `/proc`），`Reap` 静默跳过，调用方分不清"无残留"与"有残留但杀不了"；随后冷启动/监督器重启盲目 spawn → 端口碰撞或覆写 `EPERM`，日志只有事后错误；恢复屏原因写死"意外退出"，锁死与真崩溃不可区分。

## Decision

- 新增 `OrphanDshReaper.EnsureNoResidue`（判定核心，委托注入可单测）：读 `.dsh-pid` 记录 → token 复验命中则经既有 `Reap` 杀，杀后仍活 → `Unreapable`；复验不中 → 判活（`RuntimeLineageProbes.TryIsAlive`）：活着 → `Unreapable`（活着但验不明归属，不敢杀——零误杀），死了 → `Clear`；无记录/不可读 → `Clear`。
- 冷启动（`StartInnerAsync`）：`Reap` 调用换 `EnsureNoResidue`（同委托 + `TryIsAlive`，verified-kill 语义保留，`Reap` 仍被内部复用）；`Unreapable` → fail-loud 日志 + 跳过 spawn（`return null`，走既有"降级加载 wwwroot"路径，不静默碰撞）。
- 监督器重启：每轮先经宿主 `TryDetectUnreapableResidue` 预检（`EnsureNoResidue` 生产委托封装；verified 僵尸顺手杀）；`Unreapable` → 跳过本轮 `RestartAsync`，恢复屏带锁原因（`showRecovery` 收 `bool` 参数，真 = 锁原因，假 = 既有崩溃原因），`failedRetryDelay` 后重探（用户手动清残留即恢复）。
- 文案：`UiCopy.ReasonDshResidueLocked`（恢复屏）+ `HostLog` fail-loud 行（`[host]`/`[supervisor]` 前缀，诊断豁免）。

## Alternatives considered

- **文件独占探针统管两腿**：落败——待替换的 shell exe 壳自身即持有者（自锁不可分，`LaunchWindows` 前探针恒"被占"）；宿主恒以 PATH dsh 形态运行，不掌握二进制路径；pid 记录 + token + 判活才是精确仪器。
- **`LaunchWindows`（Inno）前预检**：落败——同自锁理由，无判别力；该腿仍靠 `/CLOSEAPPLICATIONS` + 退出兜底。
- **验不明归属直接按 pid 杀**：落败——pid 复用误杀不可逆，零误杀是收割的立命之本；不敢杀 + fail loud 是唯一安全出口。
- **冷启动 `Unreapable` 抛异常**：落败——`StartAsync` 契约返回 null（降级 wwwroot 既有路径）；改契约牵连组合根，fail-loud 日志 + 降级已足够可见，且监督器随后每轮带锁原因上屏。
- **恢复屏原因走横幅/日志代替**：落败——恢复屏是崩溃时刻用户唯一界面（横幅需 dsh 存活才可达，而恢复屏出现时 dsh 必然不可用），日志用户看不见。

## Consequences

- Linux 行为零变更（verified 路径与今日一致；unverified + dead = `Clear` 与今日跳过等价）。
- Windows/macOS 残留场景从"静默碰撞"变为"可见失败 + 可恢复"（手动清残留后下轮自动好）。
- 监督器 `showRecovery` 签名变（`Func<bool, ValueTask>`），组合根接线同步；`Reap` bool 版保留（被 `EnsureNoResidue` 复用，测试不动）。

## Testing

- `OrphanDshReaperEnsureTests` 九法（六分支 + 复验抛回退 ×2 + 判活抛 + Reap 报 false·已死）；`RecoveryPageTests` 锁原因渲染；`dotnet test` 全绿 0 警告；Windows 残留实复现待社区真机。
