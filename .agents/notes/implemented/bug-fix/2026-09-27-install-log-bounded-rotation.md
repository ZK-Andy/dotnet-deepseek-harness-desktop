# Agent Note: updates 目录 install.log 有界滚代

Status: implemented

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok

三审结论：R1/R2 0 Blocker。采纳——日志名常量单源（`UpdateInstaller.BuildLinuxScript` 改用 `InstallLogFile`，两侧改名不再可能各写各的）、新常量可见性取齐 `internal`、链接判定前置并覆盖悬空链、测试抽 `CreateOversizedInstallLog` 去重；拒绝——与 `HostLog.RotateIfNeeded` 合并（两档日志属主/代后缀/上限各自独立，且 host.log 滚动在写热路径上，合并只增风险）。R2 的「悬空链下会静默溜过」前提被实测证伪（.NET 10/Linux `File.Exists` 对悬空链返回 true），判据前置仍保留为平台无关防御并新增用例。R3 0 Blocker。采纳——同变更改写 owning ADR `self-update-prune-consumed-packages` 的「install.log 除外」事实、补 Related 三链、快照计数由「三次」订正为 n=4。

## Problem

root 安装脚本用 `nohup '<exe>' >> install.log 2>&1 &` 拉起新实例（`UpdateInstaller.BuildLinuxScript`），而 `HostLog` 每写一行都同时打 stdout——于是 `~/.dsh/updates/install.log` 不只是一次安装的留痕，而是**每次自更新重启的那个实例的整段运行时控制台输出**在累积，只增不减。

本机取证（2026-09-27 22:33，n=1 快照）：111,103 字节 / 1556 行，含 30 次安装段（`== install start`），其余主体是重启实例运行时输出——`[host]` 439 行、`[update]` 171、`[shell]` 150、`[tray]` 103、`[nav]` 47、`[supervisor]` 43。【探索性：单一实例快照，行数构成为精确计数，增速外推未证】

机制上的关键是**无上界**：一个重启实例长跑多久，它的控制台输出就写多久。此前待办的判断（「增速仍缓慢线性、零增长则维持现状」）基于四次体积快照（09-13 61.8KB → 09-15 68.6KB → 09-20 84.6KB → 09-27 111KB，n=4），看不出这一点，且要人来重量。文件属主是 root（mode 644）：应用侧无写权限、截断做不到；updates 目录属主是用户，改目录项（`rename`/`unlink`）可行——该权限机制已实测：实机把 root 属主的 `install.log` 改名再改回，前后 sha256 与 inode 一致（内容零变化，改名不需要文件写权限）。既有 `StalePackagePruner` 对账不认这个形态——`install.log` 在测试里还被显式列为「不参与清扫」的名字。

## Decision

- `StalePackagePruner` 的启动对账新增 install.log 有界滚代：`RotateInstallLogIfNeeded` 在文件大于 `InstallLogMaxBytes`（5 MiB，与 `HostLog` 的单文件日志上限同一策略）时改名为 `install.log.1`（`overwrite: true`，只保一代）→ 目录日志量有界（≈ 上限 + 一个实例增量）。
- **只改名，不截断不删除**：install.log 是 pkexec 授权失败/哈希不匹配类中止的唯一观测面（ADR self-update-pkexec-toctou），证据优先级高于省磁盘。改名能作用于 root 属主文件，是靠目录属主的 rename 权限（不需要文件写权限，已实测）。
- 日志文件名单源：`UpdateInstaller.BuildLinuxScript` 拼 `>> install.log` 的重定向路径与滚代目标同用 `StalePackagePruner.InstallLogFile` 常量——任一侧改名都不会让滚代打在无人写的文件上。
- 链接判定**先于**存在性判定：悬空链会被判为链接并留一行日志再跳过。实测 .NET 10/Linux 下 `File.Exists` 对悬空链返回 **true**（评审给的「悬空链下 `File.Exists` 为假故会静默溜过」前提被该测试证伪），但前置判定使行为不依赖这条平台语义——root 脚本的 `[ -L ]` 守卫对悬空链为真、会中止后续每次安装，此处留痕比静默通过有用（与同目录下载锁的拒链口径一致，ADR profile-lock-path-symlink-rejection）。
- 沿用既有「下载锁被持有（他实例下载中）时整体跳过」语义：持锁期间既不删包也不滚代——对账期不动目录任何文件。

## Alternatives considered

- **维持现状（人眼盯体积）**：落败——这条待办本身要人工量三次才知道增速，且「重启实例长跑即长写」在只看体积快照时不可见；把它交给机器围栏比留给下次取证可靠。
- **脚本侧滚代（`BuildLinuxScript` 里 `mv` 超限日志）**：落败——脚本只在自更新时执行，冷启动永不触发；且要再往脚本里堆大小判据与上限常量（脚本已有 `[ -L ]` 守卫等逻辑）。
- **截断保留尾部（tail N KB）**：落败——root 属主文件应用侧无写权限，截断做不到；退一步「读尾写新文件 + unlink 原文件」会让仍持有 fd 的实例继续写已 unlink 的 inode，那部分输出直接消失，比改名丢得多。
- **直接删除（unlink）**：落败——失败安装的现场就在文件里，删除等于删证据。
- **改 root 脚本把重启实例输出引向 host.log**：落败——host.log 由应用自滚，但安装链的 root 侧消息（哈希复验、包管理器输出、`install exit=$?`）会失去统一落点，等于换一个洞。

## Consequences

updates 目录日志有界（≤5 MiB + 一个实例增量），且上一代全文保留；不再需要人工盯体积。代价：恢复现场时可能要翻到 `install.log.1`；有界性是「对账式」的——两次启动之间长跑实例仍可超上限。

## Related

- [self-update-prune-consumed-packages](../architecture/2026-09-04-self-update-prune-consumed-packages.md)：updates 目录启动对账的 owning 笔记，本批同变更改写其「install.log 除外」的现状陈述。
- [self-update-pkexec-toctou](2026-08-28-self-update-pkexec-toctou.md)：install.log 作为中止唯一观测面的来源。
- [profile-lock-path-symlink-rejection](2026-09-16-profile-lock-path-symlink-rejection.md)：拒链判据先例。

## Testing

- `StalePackagePrunerTests` 新增 7 例：超限滚代保内容、未超限/缺失零动作、反复滚代只保一代、符号链接拒动（指向存在目标）、悬空符号链接同样留痕跳过（Linux 断言）、Run 接线（稀疏文件 `SetLength` 触发内建上限）、持锁时整轮跳过含滚代。
- `dotnet test`：**854/854**（166+182+506）绿；`dotnet build --no-incremental`：0 警告。
