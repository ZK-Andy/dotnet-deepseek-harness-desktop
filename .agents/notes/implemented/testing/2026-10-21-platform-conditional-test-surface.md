# Agent Note: 测试面的平台条件化（Windows/macOS 全量套件转绿）

Status: implemented

Review: FULL/2026-10-21/R1=ok R2=ok R3=ok

## Problem

windows/macos 腿改跑全量套件后（[ci-full-suite-legs](2026-10-21-ci-full-suite-legs.md)），ci run `39563444037` 在两条腿上报 42 条失败（Windows 33、macOS 9），逐条定位后**没有一条是产品缺陷**——全是测试把 POSIX 语义写死：Unix domain socket 仲裁在 Windows 按平台边界关闭、`dsh` 垫片在 Windows 经 `cmd.exe /d /s /c` 中转、`.local/bin` 路径在 Windows 用反斜杠、插件 journal 夹具用 `$$"""` 原始插值手拼 JSON（Windows 反斜杠成非法转义）、rc 文件注册在 Windows 改走 HKCU 注册表、node 发行包内 npm 布局分 `lib/node_modules` 与 `node_modules` 两形、macOS 上临时目录名加 `/instance.sock` 超出 UDS 104 字符上限。这些假设在单腿 CI 下从未被执行，红面出现后才暴露：判据「全量套件」是对的，缺的是把「平台无关断言」与「Unix-only 语义」区分开的测试设施。

## Decision

- 新增 `UnixFactAttribute`/`UnixTheoryAttribute` 与 `LinuxFactAttribute`（`tests/DeepSeek.Harness.Desktop.Infrastructure.Tests/UnixFactAttribute.cs`）：非目标平台置 `Skip` 并写明原因（xunit 2.9.3 无 `Assert.Skip`）。`Unix*` 用于产品在 Windows 上**按契约关闭**的语义（UDS 单实例仲裁、shell rc 注册），也用于调用即真改用户环境的测试设施缺口（`TryRegister()` 没有注入 seam）；`Linux*` 用于依赖 `/proc` 父链、`/bin/sh` 夹具或 Linux 内核/符号链接语义的用例——两类都写明原因。本批触及的用例全部以 Skip 属性表达平台条件（六处早退 `return` 改为跳过，另有依赖 `/bin/sh` 夹具的接力用例 base 虽无早退、在 Windows 上必然失败也一并改 Skip；八处跳过带用例专属原因——四条 `CliShimTests` 的 rc/注册用例，与 `RunMarkerTests`/`RuntimeLineageProbeTests`/两条有早退的接力用例各一条）；仓库内既有的平台分支用例不在本批范围，形态未动。
- 新增 `DshSpawnAssert`（同目录）：断言 dsh 启动命令的完整平台形状——Unix `FileName=="dsh"`，Windows `cmd.exe` + `["/d","/s","/c","dsh.cmd", ..]`（[windows-dsh-spawn-unified](../bug-fix/2026-09-27-windows-dsh-spawn-unified.md)），把 5 处 `Assert.Equal("dsh", psi.FileName)` 收敛到一处判据。
- 路径期望一律用 `Path.Combine`/`Path.GetFullPath` 构造（`HarnessRuntimeHostTests`、`DevEnvironmentTests`、`ArchitectureTests` 相对路径归一 `\`→`/`）；macOS 临时目录名压到 8 位（`dsh-l-<8hex>`），使 `<tmp>/<dir>/instance.sock` 落在 UDS 104 字符上限内。
- 插件 journal 夹具改 `JsonSerializer.Serialize` 生成（与产品 `WritePending` 同源），不再手拼 `$$"""` 原始插值；`RuntimeBootstrapTests` 的假发行包按平台造 `node_modules` / `lib/node_modules` 两形，垫片回退用例按「spawn 是否指向垫片绝对路径」分流（Windows 上与主路径同为 `cmd.exe`，只能靠参数认）。

## Alternatives considered

- **给 win/mac 腿重新加回测试类过滤**：落败——那正是本批要削掉的假绿面；42 条失败里没有产品缺陷，说明过滤掩盖的是**测试设施缺失**，不是被测行为缺失。
- **在测试里写 `if (OperatingSystem.IsWindows()) return;`**：落败——静默通过会把跳过伪装成执行，CI 报告看不出覆盖缺口；`Skip` 属性让跳过数与原因都进报告。
- **按平台复制一套 Windows 专用测试类**：落败——同一不变量两套断言会漂移；改为断言工厂（`DshSpawnAssert`）+ 单点平台分支。
- **改产品让 Windows 也启用 UDS 仲裁**：落败——[single-instance-launcher-activation](../architecture/2026-08-26-single-instance-launcher-activation.md) 已把 Windows 的「不启用互斥」定为平台边界（无验证环境），本批不动产品语义。
- **把 journal 夹具改成转义后的普通字符串**：落败——手写转义在跨平台路径上仍是同一类陷阱；用产品同款序列化器才消除夹具与产品的形状差。

## Consequences

- Windows 报告中的跳过项是**有意的**：UDS 仲裁与 rc 注册在 Windows 无对应语义，跳过原因写进用例元数据。
  **已登记的覆盖缺口**：`CliShimRegistrar.RegisterWindowsPath`（HKCU `Environment\Path` 写入 + `WM_SETTINGCHANGE` 广播）没有注入 seam，调用 `TryRegister()` 的用例在 Windows 上会真改用户 PATH，故 `CliShimTests` 中 `TryRegister_Writes_Pnpm_Shim_Not_Dsh`、`TryRegister_Preserves_User_Shim_File`、`TryRegister_Is_Idempotent_For_Rc`、`TryRegister_ExposesSystemGlobalNodeBinDir` 四条在 Windows 跳过；注册表侧的读写与广播目前**零测试覆盖**，补 seam 是独立改动（`DSH_DESKTOP_CLI_RC_HOME` 只有 Unix 侧钩子）。
- 六处原本「早退 `return`」的用例改为显式 Skip：`StaleUndeletableSocket_DegradesToPrimaryWithoutListener_NotZeroInstance`、`TryRideMarketRelayAsync_AdoptsLinuxLineageSuccessor_WhenRelayTakesOver`/`_SuccessorBoundButWebNotReady_KeepsWaitingToBudget`/`_TransientWebReady_DoesNotAdoptBeforeStableWindow`、`Acquire_SymlinkedMarker_UnlinksAndReplaces_NotWrittenThrough`、`ProbeLoopbackBind_BoundButNotListening_Free_OnLinux`；`TryRideMarketRelayAsync_LiveToolRunnerDescendant_IsNotEvidence_BailsOnGrace` 在 base 无早退，但以 `/bin/sh` 为夹具、Windows 上必然失败，同样改 `LinuxFact`。Windows/macOS 报告里它们从「通过」变「跳过」，覆盖口径更诚实——macOS 上 UDS 用例照常执行，而血统与 Linux 内核语义几条本就是 Linux-only（`RuntimeLineageProbes.ReadParentPid` 非 Linux 恒 `null`，断言恒真）。
  **保留的运行期早退**：`StaleUndeletableSocket…` 内的 root 权限探针 `if (!permsEnforced) return;` 属夹具前提（xunit 2.9.3 无运行期 Skip）：权限模型不生效（root）时早退、报告显示「通过」，非 root 环境才走断言，该形态未改。
  **未变的既有形态**：`StalePackagePrunerTests`、`PluginProfileTransactionTests` 的符号链接组、`SystemBrowserTests`、`PathLinkGuardTests` 等十三条 `if (!OperatingSystem.IsLinux()) return;` 形态，加上 `DshLoopbackProxyTests.cs` 的 Windows 分支早退（共十四处）——Windows 上这十四处空过后仍记为「通过」（其中十三条 `!IsLinux` 形态在 macOS 上同样空过；`HarnessRuntimeHostTests.cs:840` 早退前已断言首个 Uri，只跑了平台无关的前半段），报告看不出未执行，与本 ADR 否定的静默通过同形；属既有的覆盖缺口，改造是独立批次。`LauncherActivationTests` 另有三条 Windows 分支先断言契约再返回，属有意的双分支，不在此列。
- macOS 的 UDS 用例恢复执行，临时根长度不再是被测不变量的一部分——**代价**：若未来 macOS 临时根再变长（如更长的 per-user 目录），短名只提供余量、不是保证，红面会重新出现且指向真实上限。
- `DshSpawnAssert` 把「Windows 垫片中转」写成测试判据后，产品若改启动形态（如改用 `busybox` 或直接解析 `.cmd`）会集中红在一处，属预期的契约测试。
- 八条断言（`HarnessRuntimeHostTests` 5 个 case、`DevEnvironmentTests` 1、`ArchitectureTests` 2）从「POSIX 字面量」换成平台构造，Linux 腿的判据不变（1093 条仍全绿、零跳过）；Windows/macOS 腿的预期读数需以真实 run 为准【推断 · 未证】，首个执行点即下一次 `ci.yml` push run。

## Testing

- Linux 腿全量：`Desktop` 237、`Core` 234、`Analyzers` 6、`Infrastructure` 616，合计 1093 全过、0 跳过（与 [`scripts/test-baseline.json`](../../../../scripts/test-baseline.json) 同值）。
- `dotnet build` 零警告；`dotnet format --verify-no-changes --severity warn` 通过。
- Windows/macOS 腿的转绿由 push 后的 ci run 验证（`5103348` 的 `39563444037` 为红基线的对照）。
