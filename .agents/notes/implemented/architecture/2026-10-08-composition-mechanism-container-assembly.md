# Agent Note: 组合根机制收官——容器组装对象图，接线层只剩触发

Status: implemented

Erratum: 2026-10-15 — Problem 段落点换家：`DesktopBootstrap.App.cs`/`DesktopBootstrap.cs` 已改名 `DesktopAdapter*.cs`，`Supervision.cs` 现名 `Bootstrap/StartupSequence.Supervision.cs`，按旧名与旧行号 grep 今天落空。正文与决定不动。

Review: FULL/2026-10-15#3/R1=ok R2=ok R3=ok

四步落地篇（按序）：[runtime-starter-core-usecase](2026-10-08-runtime-starter-core-usecase.md)、[startupwiring-deletion-run-singletons](2026-10-08-startupwiring-deletion-run-singletons.md)、[root-fields-zero-shared-state-singletons](2026-10-08-root-fields-zero-shared-state-singletons.md)、[desktop-adapter-rename-final-assembly](2026-10-08-desktop-adapter-rename-final-assembly.md)、step-1 的 [startup-usecases-sink-to-core](2026-10-15-startup-usecases-sink-to-core.md) 与 [startup-sequencer-shape](2026-10-15-startup-sequencer-shape.md)；残留批 [sequencer-stage-value-retirement](2026-10-15-sequencer-stage-value-retirement.md)（波 A）与 [sequencer-resolution-injection](2026-10-15-sequencer-resolution-injection.md)（波 B）；收口篇 [app-exit-orchestration-core-usecase](2026-10-15-app-exit-orchestration-core-usecase.md) 与 [entry-bridge-two-phase](2026-10-15-entry-bridge-two-phase.md)。

## Problem

两轮重构（值流批次 0–3：字段 32→18→10、产出类型化、`LaunchOptions` 的 `IOptions` 化；`df797cc` 形态分离：注册下沉 `AddXxx`、编排搬出根成 `IStartupSequence`；`0497284` 换闸）治的是**形态**（文件位置、字段个数），没治**机制**（谁 `new` 谁）：

- **容器当字典用**：`DesktopBootstrap.App.cs:139-149` 的工厂闭包 `new StartupSequence(…)` 收十个参数，全是根手头已有的具体对象，容器只在期末 `GetRequiredService` 取一次；根手里还留着 `_timeouts/_captionBar/_uiLocale/_shellForward/_instanceListener`（`DesktopBootstrap.cs:16-26`）与手搓 locator `StartupWiring`——每个新功能加一个字段。真正的 .NET DI 是反过来的：注册各服务的构造依赖，让容器解图。
- **`StartupSequence` 是流亡的组合根**：构造器收十个具体类型（`StartupSequence.cs:16-27`）；`Supervision.cs:147` 的 `SetupCaptionBar` 直接拼脚本内容（域决策，非接线）；`StartRuntime` 的 `GetAwaiter().GetResult()` 阻塞起宿主、铸币、收 URL（用例编排，不可单测）。对照组是同根的 `UpdateCoordinator`（住 Core、四端口注入、可单测）。
- **仪表盘读数**：`verify-compose-root.py` 的 `new 自有类型 16/16` 顶格——不是偶发超标，是结构必然：机制不变，加功能就加 `new`。
- **真正的结**：`RynApplication.CreateBuilder().ConfigureServices(…).Build()` 拥有容器、`App.Run()` 阻塞，没有 Generic Host 的 `IHostedService` 位置。"编排器必须由根显式触发"是实话，但被读成了"所以必须手搓 wiring"——触发只需 5 行，装配本该全进容器（当时是触发 5 行 + 手搓装配约 500 行，`StartupSequence*` 四文件）。

## Decision

不做第三轮"再搬一次家"，换机制；四步均已落地：

1. **用例下沉 Core**：`IWindowChromePolicy`、`IRuntimeStarter`、导航（`Core.Ui.MainUiEntry`）、监督（`Core.Bootstrap.RuntimeSupervision`）、顶栏注入（`PageBridge.CaptionBarInstaller`）与启动期告知（`Infrastructure.Bootstrap.StartupNoticeService`）各成用例，全部构造注入、可单测。`StartupSequence` 是**排序器**：两相入口 `RunStartupAsync`/`RunMainLoop` 按序触发用例/阶段，不 `new` 具体类、不拼字符串、构造参数经容器解图，接线层只显式供给容器装不下的 Build 后应用产出（Ryn 不登记 `RynApplication`）；同步桥只余接线层入口一处，两相切分与闸改向见 [entry-bridge-two-phase](2026-10-15-entry-bridge-two-phase.md)。
2. **根字段归零**：`CaptionBarOptions/RuntimeTimeouts` 与共享单例经容器消费（`LaunchOptions` 是样板）；`StartupWiring` locator 已删，生命周期还给容器；`DesktopAdapter` 只剩容器之前的启动头部 + 唯一 dot 分部的按域 `AddXxx()` 注册。
3. **命名诚实化**：`DesktopBootstrap` 已改名 `DesktopAdapter`（Ryn 适配接线层；真正的组合在 Core）。`verify-compose-root.py` 断言接线层只引用端口 + Options + 适配器 + 冻结装配面（C5 方向），并断言编排器保持排序器形状（C6）；构造计数同时覆盖 `new` 与 DI 工厂（`CreateInstance<T>`）两条通道。
4. **顺序**：用例先行 → 删 locator → 根字段归零 → 改名与闸改向 → 排序器收尾与 C6；每步 FULL 评审只审 R1/R3。

## Alternatives considered

- **第三轮搬家（再拆分部/再减字段）（落败）**：前两轮证明字段可压缩但归零不了；机制不变，`16/16` 会再顶格。
- **Generic Host 全接管（不可行）**：Ryn 拥有 `RynApplication` 构建器与 `Run()` 主循环，`Host.CreateApplicationBuilder` 无法成为外层；只能是"Core 编排 + Ryn 适配"。
- **维持现状 + 放宽闸阈值（落败）**：把 `16` 放到 `20` 只是把报警器调低，趋势不改；而跳过"用例下沉"直接改名与改闸，则编排器本身仍无形状约束。

## Consequences

- 买到的：新功能不再加根字段；导航/监督/起步/顶栏/告知各有用例家（`MainUiEntryTests`/`RuntimeSupervisionTests`/`RuntimeStarterTests` 覆盖）；门禁从"尺寸闸"变"方向闸 + 形状闸"（C5/C6），接线层与编排器都不再随功能顶格。
- 付出的：`IRuntimeStarter` 抽取触碰启动最敏感链（spawn→铸币→导航），全链实机取证已随 v0.6.9/v0.6.10 装机闭环（2026-10-15）；入口桥的两相化（异步 I/O 相 + thread 0 同步相）见 [entry-bridge-two-phase](2026-10-15-entry-bridge-two-phase.md)，其线程面同属该次实机取证（2026-10-15 随装机走通）；容器装不下的 Build 后应用产出（`AppSetup`）仍由接线层显式供给。本机制的第一批住户见 [window-chrome-depatch-core-ports](../simplification/2026-10-08-window-chrome-depatch-core-ports.md)。
