# Agent Note: profile-lock-path-symlink-rejection

Status: implemented

Review: LIGHT/2026-09-16/R2=ok（0 Blocker、3 Suggestion 已收口）

## Problem

上游对齐台账（`.plan/upstream-alignment-plan-2026-09-13.md` §1.1/§1.2）把「锁语义三件套」列为本仓待补项，其中**拒符号链接**一项的结论是「**遗留**：profile/锁路径拒符号链接未做」。

本仓的链接防护只存在于三处，且都不在 profile/锁路径上：`RunMarker`（解链重建）、`CliShimRegistrar`（悬空链先移除）、`UpdateInstaller`（root 脚本 `[ -L ]` 拒 install.log）。而下列操作点会**穿链接**作用到链接目标：

- `PluginProfileTransaction.Activate` 的 `Directory.Move(activeProfile, rollback)` 与 `Directory.Move(staging, activeProfile)`——active profile 若是链接，移动的是链接或经链接作用；
- `PluginProfileTransaction.TryDeleteDir` 的 `Directory.Delete(recursive)`——清扫 stray staging/rollback 时若路径是链接，可能作用到链接目标；
- `MarketInstallHelper.AtomicWriteFile(_pendingPath, …)` 写事务 journal——journal 若是链接，等价于把 journal 语义写到外部文件；
- `InstallerDownloader.TryAcquireDownloadLock` 的 `File.Open(lock, OpenOrCreate, FileShare.None)`——锁若是链接，独占语义被施加到无关文件；
- `StalePackagePruner` 对锁文件的 `File.Exists` + 独占打开 + `File.Delete`。

触发场景不是「远地攻击者」：能写 `~/.dsh` 的进程已具备该用户权限。真实可达的是**本地误配**——把 profile 目录做成指向别处（例如挪到大盘）的符号链接，于是整拷/换入/清扫全部作用到链接目标；以及构造的链接把 journal 或下载锁重定向出 `DSH_HOME`。

## Decision

**只探不穿 + 在操作点拒链接**（`PathLinkGuard` 单一判定，`Infrastructure/Platform/`）：

1. 判定语义：`PathLinkGuard.IsLink(path)` 用 `FileSystemInfo.LinkTarget` 与 `FileAttributes.ReparsePoint` 双探，**重解析点属性只在路径作为对象存在时读**（缺失路径下 Unix 会返回 `(FileAttributes)(-1)`，其 ReparsePoint 位为真）；路径不存在返回 `false`；探测抛 IO/授权/平台异常时按非链接放行**并记一行 `HostLog`**——守卫不可用必须留痕，静默放行会让拒链在受限目录上无声失效。折算式独立为纯函数 `IsLinkFrom(linkTarget, attributes?)`，使 Windows 重解析点分支在所有平台可测。
2. 操作点处置——**拒，不自愈**：
   - `PluginProfileTransaction.Begin` / `Activate` / `Recover`：active profile、pending journal 为链接即抛 `InvalidOperationException`（fail loud，文案指名路径与「拒符号链接」），与既有 journal 损坏/版本不认识的 fail-loud 同款。
   - `PluginProfileTransaction.TryDeleteDir`：目标为链接则跳过并记日志（该路径本就 best-effort、不阻断主流程），绝不 `Directory.Delete(recursive)` 穿链。
   - `InstallerDownloader.TryAcquireDownloadLock`：锁路径为链接即抛 `InvalidOperationException`——不复用「返回 null」这条契约，那条 null 的语义是「他实例下载中」，用它承载链接拒绝会骗调用方。
   - `StalePackagePruner.RunInner`：锁路径为链接即记日志并整体跳过本轮清扫（fail-safe，不动任何文件）。
3. 覆盖边界：命名路径（active profile、pending journal、下载锁）在**操作点**逐个判定；guid 命名的 staging/rollback 目录不单列判定，由 `TryDeleteDir` 的删除点统一兜住。
4. **TOCTOU 窗口记明**：判定与操作之间链接可被替换，.NET 无跨平台 `O_NOFOLLOW` 等价物能与之原子化（见 Alternatives）。本决策防的是误配与已存在的链接，不宣称抵御并发替换。

## Alternatives considered

- **沿用 `RunMarker` 式「解链重建」**：落败——run-marker 是无状态取证文件，拔掉重建无损失；profile 是**用户数据目录**，静默 unlink 会毁掉用户有意建立的指向（把小盘 profile 链到大盘），且旧数据仍留在链接目标形成两处事实。数据路径必须拒而非自愈。
- **用 `O_NOFOLLOW` / 平台原子打开选项**：落败——.NET 无跨平台等价物（Windows 无此语义），且 `Directory.Move`/`Directory.Delete` 没有对应选项；只能「探后再操作」，TOCTOU 窗口因此无法在本决策内关闭。
- **只护 profile、放过锁路径**：落败——台账点名的是「profile/锁路径」两者；下载锁被重定向会把「独占」施加到无关文件，且 `StalePackagePruner` 会删掉那个被指向的对象。
- **不做，接受现状**：落败——台账已判定为遗留，且误配场景用户可达（非纯理论攻击面）；成本是一个 internal 判定 + 五处操作点。
- **接进 Roslyn analyzer / 门禁脚本机器化**：不适用——该不变量取决于运行期文件系统形态（同一路径可能此刻是链接、彼时不是），静态不可判；故落代码内判定，不留评审兜底项。

## Consequences

- **新增面**：`Infrastructure/Platform/PathLinkGuard.cs`（internal，无公共 API 变化）+ 五处操作点判定；`PluginProfileTransaction`/`InstallerDownloader`/`StalePackagePruner` 的既有行为仅在「路径是链接」这一异常形态下改变（此前穿链，现拒绝）。
- **失败语义**：事务与下载链 fail loud（异常带路径与原因）；清扫链 fail safe（跳过 + 日志）。link 存在不再导致静默写穿。
- **残余风险**：TOCTOU 窗口（判定→操作之间替换链接）在本决策内不关闭，本地受限场景接受并已在 Decision 第 4 条记明；`File.Delete` 对文件链接本就只删链接，故未加护（`StalePackagePruner` 的包删除路径不涉及链接穿透）。
- **测试**：新增 15 例（基线 641 → 656）——`PathLinkGuardTests` 的折算式 `Theory`（含 Windows junction 分支，全平台）+ 实链用例（文件链/目录链/悬空链，仅 Linux 断言：建链需特权）+ 缺失路径回归 + 事务 Begin/Activate/Recover 拒链 + 下载锁拒链 + 清扫跳过（断言日志与链接目标完好）。
- **实现陷阱（首版踩中，已由回归钉死）**：判定必须**先确认路径存在、再读属性**。Unix 下 `FileInfo.Attributes` 对缺失路径返回 `(FileAttributes)(-1)`，ReparsePoint 位为真——按 `LinkTarget || Attributes.HasFlag(ReparsePoint)` 直读会把每个不存在的路径判成链接，后果是**全新安装时缺失的下载锁被当链接拒绝**。`IsLink_MissingPath_ReturnFalse` 全平台运行，钉住此坑。
- **上游对齐**：台账 §1.1 第 1 项「拒符号链接」由此核销；owner 移交与 pid 探活按台账结论仍不适用本壳（无长子进程持锁场景）。

## Related

- `.plan/upstream-alignment-plan-2026-09-13.md` §1.1/§1.2：本决策的来源台账（符号链接一项的遗留结论）。
- `RunMarker`（implemented/architecture/2026-08-24-shell-observability-diagnostics）与 `CliShimRegistrar`：本仓既有的另两处链接处置，语义刻意不同（彼处解链重建/悬空链移除）——差异理由见 Alternatives。
- `PluginProfileTransaction`（implemented/architecture/2026-09-13-transactional-plugin-pipeline）：被加固的事务管线。
- `cross-process-filelock-semantics`（implemented/testing/2026-09-13）：下载锁与事务并发的既有决策，本笔记不改其互斥形态。
