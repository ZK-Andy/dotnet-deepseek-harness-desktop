# Agent Note: restructure-compose-root-final-moves（余批 3：#5/#7 出根 + PayloadSmoke 归位，结构整改收官）

Status: implemented

Review: FULL/2026-09-29/R1=ok R2=ok R3=ok

三审处置：R1 0 Blocker 5S、R2 0 Blocker 2S、R3 1 Blocker（Consequences 行数数字面）已修。S 采纳：csproj 注释精简、payload-smoke-probe 路径跟改、XDG 缺失分支补钉、Related 补链、收官三件（总纲迁 implemented）。S 驳回记挂账：DSH_DEVTOOLS 散读下沉、SocketPath/FallbackUidSuffix 降 internal（HANDOFF-todos）。

中文（双语暂不启用）。

## Problem

结构整改总纲（`process/2026-09-27-post-packaging-churn-restructure`）的最后三块，全部命中 FULL 触发面，按评审档经济学合并一批只付一次三审：

- **#5 单实例 socket 拼装**（`DesktopBootstrap.cs` `AcquireSingleInstance`）：XDG_RUNTIME_DIR 回退临时目录 + uid 后缀防跨用户抢占 + Windows 平台分支 + dev 分域——平台策略内联在组合根；`LauncherActivation` 已有 `SocketPath`/`FallbackUidSuffix` 原语，缺的是「谁拼装」。
- **#7 `--export-diagnostics` 分支**（`Program.cs:17-42`）：开关判定 + 导出执行 + 异常收口约 40 行 CLI 用例内联在入口；诊断导出实现（`DiagnosticsExporter`）本就在 Infrastructure，缺的是 CLI 面收口。
- **G 仅余项 PayloadSmoke 误分类**：`tests/DeepSeek.Harness.Desktop.PayloadSmoke` 是 `OutputType=Exe`、零 ProjectReference、零 `[Fact]` 的打包期 native 载荷探针（publish 后由 package.yml 调用），挂在 `tests/` + slnx `/tests/` 分组是误分类。

## Decision

1. **`LauncherActivation.ResolveInstanceSocketPath(isDev)`** 为锁地址解析平台策略单源：Windows 返回 null（不启用仲裁）；Unix 为 `XDG_RUNTIME_DIR`（缺失回退 `Path.GetTempPath()` 并掺 `FallbackUidSuffix()`）下 `SocketPath(...)`；应用名字面量收为类内私有常量。组合根 `AcquireSingleInstance` 只剩一行解析调用 + 仲裁。
2. **`DiagnosticsCli`**（Infrastructure/Platform）为 CLI 诊断导出单源；失败路径保留 `Console.Error`（stderr 用户契约：CLI 面退出码即失败信号，HostLog 走 stdout+落盘不等价），`verify-code-conventions.py` 的 D004 白名单随文件名跟改（`Program.cs` → 增列 `DiagnosticsCli.cs`，同一「入口诊断」豁免语义）：`ArgExportDiagnostics` 常量（用户契约 `--export-diagnostics` 逐字节不变，docs/user-guide 双语记载）+ `IsRequested`（纯函数）+ `TryRun(args)`（请求即执行返回退出码，非诊断返回 null）+ `Run`（原 `Program.ExportDiagnostics` 语句序/异常边界/日志逐字节对应）。入口 `Main` 收敛为 `DiagnosticsCli.TryRun(args) ?? new DesktopBootstrap().Run()`。
3. **PayloadSmoke 移 `tools/`**：`git mv` 至 `tools/DeepSeek.Harness.Desktop.PayloadSmoke/`，slnx 新增 `/tools/` 分组（移出 `/tests/`），package.yml 两处 publish 路径跟改，csproj 注释更正归属。探针二进制名/用法/子进程协议零变化。

## Alternatives considered

- **#5 顺带把 `TryBindPrimary`/`NotifyPrimary` 也收进一个实例类**：仲裁编排（bind 探活/通知/降级）已住在 `LauncherActivation`，根上剩的只有调用与早退——再收只是换地址。落败。
- **#7 采用 `IsRequested`+`Run` 两步留在 Main**：入口保留 if 分支仍是一处 CLI 语义；`TryRun` 形态让入口单语句、CLI 策略（含开关字面量）全在单文件。采纳 `TryRun`。
- **PayloadSmoke 移 `src/`**：它是打包工具不是产品源码，`tools/` 语义准确。落败。
- **PayloadSmoke 出 slnx**：出组即脱离 CI 的 `dotnet format` warn 级校验覆盖（csproj 注释明言依赖该覆盖）。落败——留 slnx 新 `/tools/` 分组。

## Consequences

- 组合根再缩：`Program.cs` 43→16 行（单语句入口）、`DesktopBootstrap.cs` 少一段平台拼装；组合根内不再有任何平台策略/CLI 用例内联。
- CLI 契约零变化（开关字面量、退出码语义、stdout/stderr 口径逐字节保持）；单实例锁地址解析零行为变化（新测试钉 Windows null/Unix XDG 分域）。
- PayloadSmoke 归位后 `tests/` 下全部为真测试工程；探针调用路径更新为 `tools/`（package.yml 两处）。
- 测试 +4（`ResolveInstanceSocketPath` 平台钉 1 + `DiagnosticsCli` 路由契约 3），基线 878/878。
- FULL 触发面付费一次（package.yml + 组合根两文件 + 移位 Program.cs 共 4 触发器）。

## Related

- 总纲：`implemented/process/2026-09-27-post-packaging-churn-restructure`（本批收官；随本批改写 implemented）
- 余批 1：`implemented/process/2026-09-29-restructure-fine-moves-and-test-relocation`
- 余批 2：`implemented/architecture/2026-09-29-restructure-proxy-header-policy`
- 单实例仲裁：`implemented/architecture/2026-08-26-single-instance-launcher-activation`
- 入口形态前史（61 行 Main 的首次分离）：`implemented/architecture/2026-08-30-split-program-main-god-function`
