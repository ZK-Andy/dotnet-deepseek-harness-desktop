# Agent Note: split-program-main-god-function（拆分 Program.Main God function）

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`Program.Main` 是 ~1300 行的 God function（`Program.cs` 1337 行）：把运行时定位、dev 隔离、单实例仲裁、profile 自举/reconcile、companion spawn 前安装、CLI shim、StartAsync、自更新栈、托盘/IPC 命令路由、崩溃监督、健康探针、引导后台任务等塞进一个方法，~25 个共享状态靠闭包局部函数捕获。

后果：①违背 SRP，任何横切改动（如 v0.4.0 的 PATH-dsh companion 回归）blast radius 大；②局部函数与外层变量深度耦合，难审、难单测；③无法满足本仓 `docs/coding-standards.md` 的"方法简洁"目标，且一眼不可读。

## Decision

**把 `Main` 的编排逻辑抽取到 `DesktopBootstrap` 组合根类，`Main` 瘦身为薄壳；共享状态降为类字段，子域逻辑降为小方法。不变量 = 纯抽取、零行为变更**（不改控制流顺序、不改任何逻辑/分支/异常边界；`dotnet test` 保持 464/464 全绿）。

- **`DesktopBootstrap`**：持有共享状态字段（bootstrapGate/preinstallGate/supervisorCts/updateWindow/instanceListener/maximizedAtHide/marker/…），`Main` 只做「参数解析 + 实例化 + Run」。
- **子域小方法**：每个顶层子块抽成私有方法（`ResolveDevAndRuntime`/`AcquireSingleInstance`/`EnsureDesktopProfile`/`InstallCompanionBeforeSpawn`/`RegisterCliShim`/`InitUpdateStack`/`SetupTrayAndRoutes`/`RunBootstrapTask`/`StartSupervisor`…），方法体保持 ≤30 行目标（内部再拆）。
- **文件拆分**：`DesktopBootstrap` 用 partial 文件（`DesktopBootstrap.Profiles.cs` / `.Update.cs` / `.Tray.cs` / `.Bootstrap.cs`…）确保各文件 ≤1000 行。局部函数（`RunDshPluginAddAsync`/`RunDshCommandsAsync`/`RunPreinstallStreamAsync`/`RecallAsync`/`StartQuitWatchdog`/`StartExitFallback`/`HideForTrayAsync`…）转实例方法。
- **验证**：每抽取一批就 `dotnet build + dotnet test` 保持绿；行为级不变（纯搬移）。

## Alternatives considered

- **拆成 `App`/多个 service 类、`Main` 完全重写**：重构过猛，把组合根打散成多个类会改变既有闭包/时序语义，回归风险最高。否决——保留 `DesktopBootstrap` 作为单一组合根，只把"集中调用"变"结构化类"。
- **只把局部函数抽成静态方法、`Main` 保留**：局部函数捕获大量外层变量，抽成静态方法需把 ~25 个状态逐一堆入参数/out，签名爆炸；且 `Main` 主体仍大。否决。
- **只抽部分自包含子块**：第一步可做（如 diagnostics/profile-init），但 `Main` 只从 1300 降到 ~900，未达"瘦身"目标。否决为唯一方案——本 ADR 用完整 `DesktopBootstrap` 抽法一次到位，但按批次提交。
- **维持现状**：God function 依旧，横切改动风险不减。否决。
- **加行数校验脚本**（用户已拍板另计）：行数指标不作为编码规范，见 `csharp-coding-standard`；本 ADR 只解决 Main 结构性拆分。

## Consequences

- `Main` 瘦至 ~30 行（组合根声明 + 委托）；后续横切改动点收敛到对应小方法。
- `DesktopBootstrap`（partial）总行数可能仍 >1000（跨文件），但**单文件 ≤1000、单方法 ≤30**，满足规范目标。
- 行为零变更：464/464 测试全绿、build 0 警告、`dotnet format --verify-no-changes` 绿。
- 局部函数"闭包捕获"改为"实例方法访问字段"，语义等价（字段即原捕获变量，赋值时序沿原方法序保留）。
- 三重审核按 feature-flow 步骤 5 执行（跨模块组合根 = 重大变更）。

## Related

- [docs/coding-standards.md](../../../../docs/coding-standards.md)：方法/文件简洁目标。
- [csharp-coding-standard](../process/2026-08-30-csharp-coding-standard.md)：本 ADR 与之解耦（结构拆分布涉行数规范）。
- 仓库根 `Program.cs`：被拆分对象。
