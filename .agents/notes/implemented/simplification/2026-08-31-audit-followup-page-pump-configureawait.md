# Agent Note: audit-followup-page-pump-configureawait

Status: implemented

## Problem

上次三重审核（R1 简化 / R2 代码，随架构机械化批次落地）留有几项**未采纳**建议。用户要求复核是否应采纳。复核中逐一对照当前代码，而非照单全收或一律拒绝。

## Decision

采纳 **R1 #2**（`PushUpdateState` 并入 `PagePump`）与 **R2 #3**（`MarketInstallHelper.Json.cs` 补 `ConfigureAwait(false)`）；**不采纳 R1 #1**（`LoadUpdateMachine` 的 `install:` 委托迁 `Services/Update/` 子域）。

**R1 #2 —— 采纳（合并入 `PagePump`）**

- `PushUpdateState`（原 `DesktopBootstrap.Startup.cs`）原为 `private static void`，仅做一件事：把 `dsh-desktop-update` CustomEvent 注入页面。这正是 `Services/PagePump` 建来兜的那一类——其类文档第一句即「把 JS 注入类操作（横幅/引导进度/插件引导状态/日志回流）从组合根抽出为静态单点」；自更新状态是其漏掉的一类。
- 特征与 `PagePump` 各方法同构：`static` 无实例状态、参数为 `CurrentWindowAccessor? accessor` + 状态对象、消费方唯一（`DesktopBootstrap.Lifecycle.cs` 的 `onTransition`）、迁移零波纹。
- **行为保留**：只吞 `InvalidOperationException`（窗口未就绪即丢）不记日志；命名维持无 `Async` 后缀（内部 `_ = …EvaluateJavaScriptAsync` 为 fire-and-forget void，与 `PagePump.PushPreinstallLog` 一致）。**没有**降级为 `PushBootstrapStateAsync` 那样的 15 次重试——自更新状态机每次变化都经 `onTransition` 再推，逐次送达语义本就该丢弃，加重试反而错。

**R2 #3 —— 采纳（补 4 处 `ConfigureAwait(false)`）**

- 编码规范 `docs/coding-standards.md`「行为契约」明写：*库/非 UI 上下文用 `ConfigureAwait(false)`（桌面壳 UI 上下文除外）*。`MarketInstallHelper` 是 `Services` 下 `public static partial class`，纯文件/JSON 操作，属**非 UI 上下文**。
- 同一个 `partial class` 内两半不一致：`MarketInstallHelper.cs` 对每个 await 都 `.ConfigureAwait(false)`（9 处），`MarketInstallHelper.Json.cs` 的 4 处 await（`CleanupBogusAppDependencyAsync` ×2、`EnsureBundlesContainsAsync` ×2）全都不带。这是机器可判的契约违反，非风格偏好。补齐后消除同 partial 内部不一致 + 一处文档-代码背离。

**R1 #1 —— 不采纳（方向倒置）**

- `DesktopBootstrap.Lifecycle.cs` 的 `install:` 委托体中留在组合根的语义是 `_closeGate.ApproveExit()` / `_updateWindow?.Current?.Close()` / `StartExitFallback(ct)`——三者都依赖组合根实例字段（`_closeGate`/`_updateWindow`），是壳生命周期与该子域的**装配胶水**。
- 业务性工作（复取哈希 `InstallerDownloader.FetchSha256Async`、`UpdateInstaller.LaunchAsync`）**已**在 `Services.Update` 子域内；IPC 侧安装也由 `Services/Update/DesktopUpdateCommandRouter.RouteInstallAsync` 走 `_machine.InstallAsync()`。子域本就拥有编排环，这里只是构造函数接线，属正确归属。
- 若把委托**迁入 `Services/Update/`**，意味着把 `_closeGate`/窗口/退出兜底以回调形式注入子域——子域反向知道自己不该关心的 shell 退出编排，**依赖方向被拉反**。这与「组合根只装配」原则冲突，不做。

## Alternatives considered

- **三建议全采纳**：落败——R1 #1 是把组合根装配胶水反注入子域，方向错误；且与 R3「边界抽象完备」同向但为它单独抽接口（`CloseGate`/`StartExitFallback`/窗口访问抽象化）不值得，属另一条战线。
- **R1 #1 采纳为「抽私有方法」的装饰性重构**：可改善可读性，但原建议是「迁子域」，不是同一件事；且当前委托体在组合根内已清晰，不值得单独动作。记录以待将来若做 R3 边界抽象时一并考虑。
- **R1 #2 迁入时改成带重试**：落败——会改变语义。自更新状态经 `onTransition` 持续推送，丢弃未就绪的一次是正确行为；重试只适用于「一次性必须送达」的引导进度/横幅。

## Consequences

- 组合根再少一段页面注入面（`PushUpdateState` 归位 `PagePump` 单点）；`MarketInstallHelper` 两个 partial 的 `ConfigureAwait(false)` 行为对齐编码规范。
- 均为低风险结构性/机械改动，无用户可见行为变更。验证：`dotnet build` 0 警告、`dotnet test` **467/467**、`dotnet format --verify-no-changes` 绿、`verify-code-conventions`/`verify-code-health` OK。

## Related

- [architecture-mechanization](../process/2026-08-30-architecture-mechanization.md)（implemented）：本次复核的评审项即来自它的三重审核执行契约；R1/R2 留评审面属性不变，后续评审仍须按 AGENTS 评审检查项核对。
- [split-program-main-god-function](../architecture/2026-08-30-split-program-main-god-function.md)（implemented）：`DesktopBootstrap` 拆分源；`PagePump` 单点归属见其「组合根只装配」。
