# Agent Note: 组合根机制收官——容器组装对象图，接线层只剩触发

Status: proposed

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

两轮重构（值流批次 0–3：字段 32→18→10、产出类型化、`LaunchOptions` 的 `IOptions` 化；`df797cc` 形态分离：注册下沉 `AddXxx`、编排搬出根成 `IStartupSequence`；`0497284` 换闸）治的是**形态**（文件位置、字段个数），没治**机制**（谁 `new` 谁）。证据：

- **容器当字典用**：`DesktopBootstrap.App.cs:139-149` 用工厂闭包 `new StartupSequence(preflight, new AppSetup(…), update, _uiLocale, _timeouts, _captionBar, _shellForward, proxy, wiring, _instanceListener)`——十个参数全是根手头已有的具体对象，容器只在期末 `GetRequiredService` 取一次。真正的 .NET DI 是反过来的：注册各服务的构造依赖，让容器解图；根手里应该什么都不剩。现在根手里还剩 `_timeouts/_captionBar/_uiLocale/_shellForward/_instanceListener`（`DesktopBootstrap.cs:16-26`）+ 手搓 locator `StartupWiring`；每个新功能都要加一个字段、加一个构造参数。
- **`StartupSequence` 是流亡的组合根**：构造器收 10 个具体类型（`StartupSequence.cs:16-27`），几乎无接口；`Supervision.cs:147` 的 `SetupCaptionBar` 直接拼脚本内容（域决策，非接线）；`StartRuntime` 里 `GetAwaiter().GetResult()` 阻塞起宿主、铸币、收 URL（用例编排，不可单测）。对照组是同根的 `UpdateCoordinator`（住 Core、四端口注入、可单测）。
- **仪表盘读数**：`verify-compose-root.py` 的 `new 自有类型 16/16` 顶格——这不是偶发超标，是结构必然：机制不变，加功能就加 `new`。
- **真正的结**：`RynApplication.CreateBuilder().ConfigureServices(…).Build()` 拥有容器，`App.Run()` 阻塞，无 Generic Host 的 `IHostedService` 位置。ADR 里"Ryn 无 hosted-service 机制，编排器必须由根显式触发"是实话，但它被读成了"所以必须手搓 wiring"——触发只需要 5 行（解析一个 Core 编排器 + `Run()`），装配（lifetime、`IOptions<T>`、后台服务）本该全进容器。现在是触发 5 行 + 手搓装配约 500 行（`StartupSequence*` 四文件）。

## Proposal

不做第三轮"再搬一次家"，换机制（行为零变化，分步合入）：

1. **用例下沉 Core**：`IWindowChromePolicy`（发什么脚本，见去补丁化那篇——本篇的第一个用例）、`IRuntimeStarter`（起宿主、铸币、收 URL）、导航/监督各一个用例；全部构造注入、全部可单测。`StartupSequence` 缩成约 30 行的排序器：按序调这些用例，不 `new` 具体类、不拼字符串、不阻塞等 I/O（`await` + `CancellationToken` 贯通，按 `coding-standards` 行为契约）。
2. **根字段归零**：`CaptionBarOptions/RuntimeTimeouts` 经 `IOptions<T>` 消费（`LaunchOptions` 已是样板）；`StartupWiring` locator 删除，生命周期还给容器（Singleton/Scoped）；`DesktopBootstrap` 只剩触发 5 行 + Ryn 适配器接线。
3. **命名诚实化**：`DesktopBootstrap` 不是组合根，是 **Ryn 适配器接线层**（Ryn 拥有真正的根）；真正的组合在 Core。闸语义跟着改：不再数"根有多大"，而断言"接线层只引用端口 + Options + 适配器"（即 R1 原文"只做装配、启动、接线与兜底"——现在超的恰是"启动"里混进的"编排内容"）。
4. **顺序**：先落用例（`IRuntimeStarter` 可独立先行，`StartRuntime` 是最大的一块不可测），再删 locator，最后改闸；每步 FULL 评审只审 R1/R3。

## Alternatives considered

- **第三轮搬家（再拆分部/再减字段）（落败）**：前两轮证明字段数可压缩但归零不了；机制不变，`16/16` 会再顶格。
- **Generic Host 全接管（不可行）**：Ryn 拥有 `RynApplication` 构建器与 `Run()` 主循环，`Host.CreateApplicationBuilder` 无法成为外层；只能是"Core 编排 + Ryn 适配"，不能是"Host 替代 Ryn"。
- **维持现状 + 放宽闸阈值（落败）**：把 `16` 放到 `20` 只是把报警器调低；每加一个功能加一个 `new` 的结构趋势不改，下次还响。
- **`StartupWiring` 转正式 Scoped 服务（备选）**：若 locator 删除阻力大，可先把它转成容器管理的正式类型作为过渡；但终态仍是删除， переход形态不得超过一批。

## Consequences

- 买到的：新功能不再加根字段；`SetupCaptionBar/StartRuntime` 进 Core 后可单测；门禁从"尺寸闸"变"方向闸"，长期不再顶格。
- 付出的：`IRuntimeStarter` 抽取触碰启动最敏感链（spawn→铸币→导航），需一次全链实机（冷启动 + 引导 + 收养恢复）+ 一次 FULL 评审；`GetAwaiter().GetResult()` 改 `await` 涉及同步上下文审查（Ryn `Run` 的线程模型，属已知风险点）。
- 与本批另一篇的关系：去补丁化那篇的 Core 端口是本篇用例下沉的第一批住户；两篇同批，互为证据；任一篇单独合入仍有独立收益。
