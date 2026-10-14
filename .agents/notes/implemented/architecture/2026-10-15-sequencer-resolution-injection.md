# Agent Note: 排序器解析面注入与 C3 压舱——静态域入口离场、根构造降到六项

Status: implemented

Review: FULL/2026-10-15#5/R1=ok R2=ok R3=ok

组合根残留批的波 B（腿 3/4/5 合并）。母篇见 [composition-mechanism-container-assembly](2026-10-08-composition-mechanism-container-assembly.md)，前序见 step-1 三篇（[startup-usecases-sink-to-core](2026-10-15-startup-usecases-sink-to-core.md)、[startup-sequencer-shape](2026-10-15-startup-sequencer-shape.md)、[sequencer-stage-value-retirement](2026-10-15-sequencer-stage-value-retirement.md)）。

## Problem

step-1 之后排序器仍有三笔账：

- **服务定位器换了宿主**：九处 `_app.App.Services.GetRequiredService<T>()`（宿主/监督/退出/主界面/顶栏注入/启动期告知/健康观测/托盘控制器/托盘服务）就在编排语句里解析——组合根不再持提供者，排序器持着。解析点本身承载两处硬时点：宿主首次解析即建宿主（须晚于 profile 前置、早于随包安装与 spawn），页面面用例必须窗口就绪后才可解析（`IRynWebView`/`CurrentWindowAccessor.Current` 原生建窗前不可碰）。
- **两处静态域入口**：`ProfileLifecycle.EnsureReady()` / `CompanionPreSpawn.EnsureInstalled(...)` 是 R1b「基础设施具体类型只许出现在注册处」的静态调用面，在 11/11 满帽里占两条。
- **C3 顶格**：共享态（`UiLocale`/`DesktopUiLocaleStore`/`DshShellForward`）、引导装配（`FirstBootBootstrapService`/`FirstBootUi`/`Preflight`）、关窗闸门与自更新栈与托盘控制器、运行时起步用例全在组合根手写构造——`new 自有类型 16/16` 与帽齐平，加一个装配期产物就红。

## Decision

1. **静态域入口实例化**：`ProfileLifecycle`/`CompanionPreSpawn` 改 `sealed` 实例类，注册处登记单例、排序器构造注入；方法体与调用契约（含「宿主已建、spawn 前」偏序）逐句不变，调用点仍在主链同一语句位。
2. **运行期解析面收成构造数据**：新增 `Bootstrap.RuntimeResolutions`（11 项：两个用例实例 + 九个 `Lazy<T>`），由 `StartupUseCaseRegistration` 装配、排序器构造注入。排序器从此不持 `IServiceProvider`；`Value` 落在原 `GetRequiredService` 的同一语句位，时点等价。平铺九个 `Lazy<T>` 参数被否（全平铺得 18 项构造表，正是母篇 Problem 记的病象）；三组阶段包亦被否（解析面尚未长到需要，留「再长时按阶段拆组」指针）。
3. **C3 压舱**：引导装配、关窗闸门/自更新栈/托盘控制器、运行时起步用例的构造移进 `Bootstrap/PreBuildRegistration.cs`——它们必须在 Build 前就位（引导服务要宿主/窗口提供者），装不进解图；闸的 C5 文案本就给出这条出口（`*Registration.cs`，即 R1b 豁免的注册层）。**装配期共享态留在组合根**：它是「容器之前头部」的一步，壳转发器在 Build 前即被代理启动消费，且它具有跨层引用密度（四个 Infrastructure 类型 × 多行）——搬进注册层会新增七条跨层引用、直接打穿增量闸的 fanout 帽。结果：**C3 16→6**（帽 16→6，清单封闭：新增根构造须动清单 + ADR），`ROOT_CONSTRUCTIBLE` 收成六项（触发器 `DesktopAdapter`、共享态三件、Build 产物 `AppSetup`、排序器触发 `StartupSequence`），`ALLOWED_METHODS` 减两项（`ResolveRuntimeAndDev`/`InitCloseGateAndUpdateStack` 不再住在组合根）。
4. **闸收口**：新增 **C7「装配期构造清单」**——`Bootstrap/PreBuildRegistration*.cs` 的自有类型构造必须 ∈ `PREBUILD_CONSTRUCTIBLE`（封闭，重开须 ADR）。C3 只数根集，被搬走的构造若无 C7 就落在所有闸的视野之外（R1/R2 同点此结）；C7 让它们留在册、两条构造通道（`new`/DI 工厂、target-typed `new()`）同待遇。自证：C3/C6/C7 与清单改动随 `--self-test` 重跑全绿。

## Alternatives considered

- **平铺九个 `Lazy<T>` 构造参数（落败）**：排序器构造表 8→**18** 项（把 `RuntimeResolutions` 的 11 项全平铺进构造器），正撞母篇 Problem 对「构造器收十个具体类型」的原始判断——把服务定位器换成参数爆炸不是改善。
- **按启动阶段拆三组解析包（落败）**：解析面只有 11 项，分组只是把同一份数据多套一层；真长起来时再拆（指针留在 `RuntimeResolutions` 类文档）。
- **让页面面用例提前构造、只做「构造注入」（落败）**：`IRynWebView` 是 Ryn 侧延迟解析、`CurrentWindowAccessor.Current` 原生建窗前即抛——构造提前等于启动即抛，比服务定位器更坏。
- **保留静态域入口，改闸允许（落败）**：R1b 的意图正是把跨层直调收进注册处；改闸等于给现状背书。
- **装配期构造留在组合根，只把 C3 帽调高（落败）**：母篇 Alternatives 已判「把 16 放到 20 只是把报警器调低，趋势不改」。
- **把装配期共享态一并搬进注册层（落败）**：共享态由四个 Infrastructure 类型构造（`RuntimeTimeouts`/`CaptionBarOptions`/`DesktopUiLocaleStore`/`DshShellForward`），搬动会新增七条跨层引用、打穿增量闸 fanout 帽（19>15）；而它本就是「容器之前头部」的一步（壳转发器在 Build 前即被代理启动消费），留在根使 C3 落在帽上、清单封闭。

## Consequences

- 买到的：**C3 6/6**（清单封闭：触发器、共享态三件、Build 产物、排序器触发）；**R1b 9/11**（两个静态域入口离场，满帽解除）；排序器零 `IServiceProvider`、零静态域直调；引导/更新与托盘装配的构造集中到 `PreBuildRegistration` 一处。
- 付出的：`RuntimeResolutions` 多一层构造数据；`PreBuildRegistration` 承接三类装配搬迁（语句逐句等价，仅文件归属变化）；`PreBuildRegistration` 的构造另立 C7 封闭清单（新装配须动清单 + ADR）。
- 时点等价性：所有 `Value` 与原 `GetRequiredService` 落在同一语句位；共享态/引导装配/更新栈装配的创建点仍在 Build 前同一调用序。

## Testing

- `dotnet test` 全绿 0 警告：**1063**（+0；纯结构搬迁）。
- 机械门禁 `--enforce` 全绿：`verify-compose-root`（C3 **6/6**、C6 零违例、白名单空集、**C7 零违例**；`--self-test` 夹具全绿）、`verify-registration-discipline`（静态调用 **9/11**，原 11/11）、`verify-code-conventions`、`verify-code-health`；增量闸 gross **405/600**、fanout **12/15**、file-max +19/150。
- 读数口径：受影响篇目同批补 Erratum——[sequencer-stage-value-retirement](2026-10-15-sequencer-stage-value-retirement.md)、[startup-sequencer-shape](2026-10-15-startup-sequencer-shape.md)、[startup-usecases-sink-to-core](2026-10-15-startup-usecases-sink-to-core.md)、[recovery-navigation-gate-retirement](2026-10-14-recovery-navigation-gate-retirement.md)、[desktop-adapter-rename-final-assembly](2026-10-08-desktop-adapter-rename-final-assembly.md)、[coupling-increment-baseline](../process/2026-10-12-coupling-increment-baseline.md)；最新读数以本篇为准，母篇 Problem 的 16/16 是机制病象描述、不改。
- **实机未验**：沿用母篇的全链实机挂账（零行为变更，由实机批次销账）。
