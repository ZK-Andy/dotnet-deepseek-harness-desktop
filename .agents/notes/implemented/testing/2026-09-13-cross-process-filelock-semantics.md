# Agent Note: 下载锁/清扫的跨进程 FileShare 语义补测与三平台测试腿

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`StalePackagePruner` 的「持锁即整体跳过」与 `InstallerDownloader.TryAcquireDownloadLock` 的「被占即 null」都建立在 `FileShare.None` 的**跨进程**互斥上，但测试只有同进程模拟（测试进程自己持句柄再调被测代码）——OS 级跨进程语义从未被验证。生产可达面在 Windows（多实例可达；Linux/macOS 单实例仲裁恒先行于更新栈），而 CI `build-test` 只在 ubuntu 跑，Windows 语义零验证。

## Decision

- 新增 `ExternalFileLockHolder` 测试 helper：起**独立进程**按 OS 用与 .NET 运行时同机制的锁独占文件（Unix `python3 fcntl.flock`/BSD flock；Windows PowerShell `File.Open` FileShare.None），读 `HELD` 就绪行后返回。
- 两个行为断言：`StalePackagePrunerTests.Run_HeldDownloadLockByOtherProcess_AbortsEntireSweep`（+ 释放后恢复的对照例）、`ReleaseAssetTests.DownloadLock_ReturnsNull_WhenHeldByOtherProcess`。
- `ci.yml` `build-test` 改三平台矩阵（ubuntu/windows/macos，`fail-fast: false`）：format 门禁与覆盖率基线只留 ubuntu 腿，windows/macos 为纯测试腿（跑 `StalePackagePrunerTests`/`ReleaseAssetTests` 两类全量测试）。

## Alternatives considered

- **python `fcntl.lockf` 做子进程持锁**：落败——实测与 .NET 互不相干（POSIX 锁与运行时锁机制不同域），持了也白持；`fcntl.flock`（BSD）才与 .NET Unix 的 FileShare 实现互斥。
- **子进程重跑测试程序集当持有者**：落败——第二 testhost 启动 5–10s 且依赖 xunit 过滤行为，比一个 5 行 python/powershell 进程贵一个量级。
- **只在 Windows 腿跑新测试、Unix 不断言**：落败——本机实测 Linux 上 .NET 确实强制跨进程 FileShare.None（CONFLICT 复现），语义全平台一致，断言三平台同构反而把平台差异锁进回归面。
- **不加 CI 平台腿**：落败——测试写了没人跑等于没写；托管 runner 的 Windows 内核就是「无真机」下唯一可信的语义验证面。

## Consequences

- 收益：下载锁/清扫的跨进程防线在三平台内核上持续回归；Windows 可达面首次有验证覆盖。
- 代价：`build-test` 从 1 job 变 3 job（每腿几分钟 runner 成本）；`ExternalFileLockHolder` 依赖 runner 的 python3（unix）与 powershell（windows）——均为 runner 预装。
- Linux 实测口径（2026-09-13，本机 dotnet 10）：跨进程 `FileShare.None` 强制互斥成立；机制为 BSD flock 域（`fcntl.lockf` 不冲突）【探索性，n=2】。

## Testing

本批自体：3 个新测试本机全绿；三平台腿由 CI run 实跑验证（见提交后的 run 号）。

## Related

- [覆盖率基线取 CI cobertura 实测](2026-09-12-coverage-baseline-from-ci-cobertura.md)：ubuntu 腿独占覆盖率基线来源，矩阵化后口径不变。
- [workflow 事件输入插值经 env: 收口](../process/2026-09-13-workflow-input-env-interpolation.md)：本次改 build-test 矩阵时同样遵守的 run: 插值纪律（本批无新增插值）。
