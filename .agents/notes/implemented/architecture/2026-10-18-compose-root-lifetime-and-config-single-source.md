# Agent Note: 组合根寿命与配置单一来源——ShellResources + SharedState

Status: implemented

Review: FULL/2026-10-18/R1=ok R2=ok R3=ok

## Problem

五处结构债（读数取动手前实测，n=1，【探索性】）：

1. **进程级资源没有唯一拥有者。** 释放面散在三处：根 `finally`（宿主、代理）、`using`（监督器取消令牌源）、`ExitPipeline`（单实例监听器、run-marker、窗口）。后果是**根必须 eager 解析 `HostSetup` 才拿得到释放句柄**——动机是释放而非使用；宿主与 run-marker 的创建因此被提到 `ProfileLifecycle.EnsureReady()` 之前，与排序器注释声明的次序分叉。异常路径（Build 抛、序列抛）监听器不释放，靠进程死亡兜住。
2. **配置有八个独立解析点。** `RuntimeTimeouts.Load` 出现在根、`PagePump` 与 6 个 Infrastructure 静态字段；其中 7 处是同一个 `private static readonly s_timeouts = Load(...)` 抄写模版。`architecture.md` 的"单点解析"只对 `LaunchOptions` 成立。
3. **根上三套传值风格并存**（四元组返回、`out` 参数、结果对象）。
4. **最可能增长的扩展面压在最紧的预算上**：Ryn 装配选项挤在 `BuildApp`，离 F4 上限 60 行只剩 1 行。
5. **装配逻辑可搬出闸外**：门禁按文件名前缀认定根集，任何搬进 `Bootstrap/` 的装配文件即脱离 C1/C2。

读数：root new-of-owned 6/6、C2 方法 7 个、F3 集合计 279/500、`Run` 57 行、`BuildApp` 59 行。

## Decision

三条原则：值归值、所有权归所有权；配置一次创建、按值传递；平台缺口（Ryn 不释放容器）由自己的唯一释放者补位并写明为谁补。

- **D1 `SharedState`（`Bootstrap/StartupStages.cs`）**：`RuntimeTimeouts` + `CaptionBarOptions` + `UiLocale` + `DshShellForward` + `PagePump` 由 `PreBuildRegistration.CreateSharedState()` 在容器之前一次造好；`AddBootstrapSharedState` 收一个值登记五个单例。根删 `ResolveSharedState`。
- **D2 `ShellResources`（`Bootstrap/ShellResources.cs`）**：进程级资源的唯一拥有者，只持有/按序释放/幂等。接口面 `Track(host)` / `Attach(proxy|cts|listener)` / `Dispose()`；释放序 host → proxy → cts → listener。根 `finally` 只剩 `resources.Dispose()`，异常路径一并覆盖。`AddRunHost(ShellResources)` 在容器工厂里 `Track`，宿主仍惰性创建、寿命归它。**marker 不入**：有序退出清、崩溃留，release 归 `ExitPipeline`。
- **D3 创建序归位**：删根的 eager `GetRequiredService<HostSetup>()`，首解回到 `StartupSequence.RunStartupAsync` 的 `Host.Value`（`EnsureReady()` 之后）；脏退日志移入 `StartupNoticeService` 构造（与横幅同一 marker 消费点）。
- **D4 配置单一来源**：`CreateSharedState` 是唯一 `Load`。六处静态站点改为按值接收（`LauncherActivation.TryBindPrimary` 收两个 `TimeSpan`、`RuntimeVersionGate.ProbeAsync` 收 probe 时限、`HarnessRuntimeHost` ctor 收记录、`RuntimeLineageProbes` 两个探针收默认超时、`PluginProcessRunner.RunProbeAsync` 收 probe 时限、`FirstBootBootstrapService` ctor 收记录）；`PagePump` 由静态类改为实例（ctor 收记录），装配期造一份、四处共用。传参口径：单值站点传 `TimeSpan`，多值站点传记录。
- **D5 删 `IStartupSequence`**：零消费者（仅定义 + 一处 `implements`，解析走具体类型的 DI 工厂）。两篇引用它的 implemented ADR 同批补 Erratum。
- **D6 门禁两层级**（`verify-compose-root.py`）：`ROOT_SET`（`Program.cs` + `DesktopAdapter*.cs`）担 F3/F4/C3/C4/C5；`ASSEMBLY_SURFACE`（根集 + `PreBuildRegistration.cs`/`ShellResources.cs`/`RynOptionsProfile.cs`）担 C1/C2，C7 覆盖 `PreBuildRegistration*.cs`，新增 **C8**：`Bootstrap/` 下未声明为域或装配面的新文件即违约。C2 清单改为**逐文件绑定**（同名单词不再跨文件豁免）。
- **D7 `RynOptionsProfile`（主工程根）**：`Apply(opts, preflight, proxy, captionBar, iconPath)` 承载全部 Ryn 装配选项（无状态、不构造自有类型），`BuildApp` 只留 icon 探测 + builder 三段。

## Alternatives considered

- **`HeadSetup`：把"容器前产出"与"所有权"合成一个类型**。落败：值与释放职责是两件事，合并只因为时间上相邻；按 R6 判据，两项各有各的答案。
- **让容器释放（资源经工厂注册，靠 provider.Dispose）**。落败：Ryn 是否在 `Run()` 退出时释放 provider 未证；`AddSingleton(instance)` 与 record struct 都不是容器可释放形态；marker 清除的"仅有序退出"语义容器表达不了。
- **只做配置、不碰根**。落败：eager 解析、异常路径漏释放、三套传值风格都留着，`ResolveSharedState` 仍是第二个创建点。
- **只碰根、不动门禁**。落败：D2/D7 的文件落到 C1/C2 之外，正是 `compose-root-form-separation` 记过的"搬出去即脱保护"。
- **全局静态配置快照 `RuntimeTimeouts.Current`**。落败：服务定位器，测试面变差，把"一次创建"退化成"任意调用点都能读"。
- **`IOptions<T>` 全面化**。落败：无命名选项、无热重载、无 `ValidateOnStart` 需求；`Load` 的 fail-safe 是刻意取舍且有测试。
- **Generic Host / `IHostApplicationLifetime`**。落败：`RynApplication` 是外层宿主，`Host.CreateApplicationBuilder` 装不进去。拒绝平台形态不等于拒绝其职责——寿命由 `ShellResources` 承接。
- **事件总线 / presenter 端口 / DTO+Mapper / 声明式阶段表 / 依赖组拆分**。落败：按 R6 三问，说不出正在发生的变更，或把编译期约束换成运行期约定。

## Consequences

- 读数（落地后实测）：根集 279 → **219** 行；`Run` 57 → 53 行（其中语句 38 行）；`BuildApp` 59 → **17** 行；C3 6 → **3**/6；`RuntimeTimeouts.Load`（src）8 → **1**；`verify-registration-discipline` 静态面 9 → 8；装配面 5 个文件全部在闸内。
- 买到的：进程级资源单一释放者，异常路径也释放；配置只有一处解析，新增超时的正确写法是"加一个属性"；装配代码无闸外区域（C8 挡住"搬进未声明的文件"）；Ryn 选项有自己的预算，F4 不再决定功能落地时机。
- 付出的：启动链最敏感的一段被重排（提交内含时序安全网 `CompositionRootSequenceTests` 的锚点更新）；`ShellResources` 是新状态持有者，只有 C2 覆盖其成员名（C3/C5 是根集口径），评审守；`docs/architecture-standards.md` 因新增判据做了同批压缩（字数预算 600）。
- 增量闸（`verify-increment-discipline`）：本批 gross 946 > 600、跨层新引用 29 > 15，两项均在册（`_justify` 见下）。理由是这批是一次跨五层同构改动（Presentation 装配面 + Bootstrap 注册面 + Infrastructure 七个超时站点 + Core 端口删除 + 门禁脚本），任何拆分都会产生「不可编译的中间提交」（Presentation 新签名依赖 Infrastructure 新参数）或「无评审证据的后续提交」（FULL 证据必须随变更集新产出）。故单提交承载，超帽事实如实记账。
- 明确不做：marker 收进 `ShellResources`（语义不同）；配置校验/`ValidateOnStart`（fail-safe 是取舍）；`ILogger`（单 sink 无分级消费者）；`AcquireSingleInstance` 的 `out` 改返回值（纯形态）。

## Testing

- `dotnet test` 1048/1048（217 Presentation / 228 Core / 597 Infrastructure / 6 Analyzers）；`dotnet format --verify-no-changes` 0 差异；build 0 错误。
- 四闸 `--enforce` 全绿：`verify-code-health`、`verify-compose-root`（含 `--self-test`）、`verify-registration-discipline`、`verify-increment-discipline`；`verify-code-conventions` 与 `verify-shell-standards` 绿。
- 时序安全网更新：`CompositionRootSequenceTests.ComposeRoot_PreContainerChain_IsInContractOrder` 改为新主链（含 `CreateSharedState`/`CreateShellResources`/`Attach`/`Dispose` 锚），`BuildApp_EnablesRynWindowStatePersistence` 改读 `RynOptionsProfile.cs`。
- 评审：FULL 三路（R1 架构 / R2 行为等价 / R3 边界契约）。R2 首轮报 1 blocker（`AddRunHost` 未注入装配期 `RuntimeTimeouts`，宿主回落默认值），已修并复验 ok；R3 首轮报 4 blocker（docstring 与实现落差、两篇 ADR 缺 Erratum、docs 口径错、变更史措辞），逐条修复后复验 ok。
- 实机四链（冷启 / 二启唤回 / `kill -9` 脏退 / 正常退出）待装机验证——本批未动三平台打包面。
