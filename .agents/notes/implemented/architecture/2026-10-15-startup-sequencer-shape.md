# Agent Note: 启动排序器收官——顶栏注入用例化与构造参数 DI

Status: implemented

Review: FULL/2026-10-15#3/R1=ok R2=ok R3=ok

Erratum: 2026-10-15 — C6 的 `SEQUENCER_VALUE_TYPES` 已收紧为空集（阶段值 `SupervisorSetup` 退役，见 [sequencer-stage-value-retirement](2026-10-15-sequencer-stage-value-retirement.md)）；正文把它称作 deliberate inventory 的表述不再适用，判据与闸结构不动。Testing 节的「`--self-test` 16 条 ok……阶段值白名单」现为 19 条：原「阶段值白名单」夹具改为「unattributable target-typed `new()`」，另新增「白名单必须为空」、「装配期构造清单」（C7）与「C7 未归属 target-typed `new()`」三条夹具；该节的 run 记录本身不改。Testing 的 `C3 16/16` 读数已由 [sequencer-resolution-injection](2026-10-15-sequencer-resolution-injection.md) 的 C3 压舱降为 **6/6**。

方案母篇见 [composition-mechanism-container-assembly](2026-10-08-composition-mechanism-container-assembly.md)——本篇落其 step-1 余项 B3/B4 并把母篇转 implemented（step-1 的 B1/B2 见 [startup-usecases-sink-to-core](2026-10-15-startup-usecases-sink-to-core.md)）。

## Problem

B1/B2 之后编排器仍不是排序器：构造器收 5 个手传具体值、构造内还有三处 `GetRequiredService` 的 service-locator；顶栏注入的脚本构建与三路注入（域决策）挤在 `StartupSequence.Supervision` 分部；`new StartupNoticeService` 在编排面拼展示面闭包；接线层 `new 自有类型` 仍 **16/16 顶格**（`StartupSequence` 是其中一件）。更根本的是闸只盖组合根集合——编排器本身没有形状约束，排序器可以再长成一个根，而本轮四步的终点恰是「排序器只排序」。

## Decision

- **顶栏注入用例化**：脚本构建（`CaptionBar.Build` + `MacNativeChrome` 判据）与三路注入（常驻到达钩子、当窗即注入、每页加载注册）自编排面搬进 `PageBridge.CaptionBarInstaller.Install()`，语言/参数/窗口/回调经构造注入；编排器只留 `InstallCaptionBar()` 一行解析触发。
- **启动期告知用例化**：`StartupNoticeService` 的装配（含两条横幅闭包与脏退标志）移进 `StartupUseCaseRegistration.AddStartupUseCases`，宿主惰性解析（调用点晚于编排首个 `HostSetup` 解析，marker 语义与原时点等价）；编排器只按序触发。
- **构造参数改 DI**：`StartupSequence` 的构造参数（`Preflight`/`UpdateSetup`/`InstanceListenerSetup`/`IRuntimeStarter`/`RuntimeTimeouts`）由容器解图，`Preflight`/`UpdateSetup` 以实例登记（`AddStartupStageValues`）；接线层不再逐个手传，`new Bootstrap.StartupSequence(...)` 收成 `ActivatorUtilities.CreateInstance<Bootstrap.StartupSequence>(builtApp.Services, app)`。唯一显式供给项是 Build 后才存在的 `AppSetup`——Ryn 不把 `RynApplication` 登记进容器（`RynApplicationBuilder.Build()` 只在末尾 `new` 出实例，v0.38.0 源读确认），它装不进解图；Build 后 holder 与 step-2b「根字段归零」相悖，不取。可空的 `PrimaryListener?`（DI 表达不了）以 `InstanceListenerSetup` 值记录过容器，编排尾部释放锁地址的非 orderly 路径语义不变。
- **C6「排序器形状」进闸**：`verify-compose-root.py` 新增 C6，扫描面 `Bootstrap/StartupSequence*.cs`——`Run()` 无控制流、无引号；排序器文件内 `new` 自有类型须在 `SEQUENCER_VALUE_TYPES`（阶段产出值；服务走容器或注册处）；同步桥 ≤1（唯一合法点＝起步桥）。同批把 **DI 工厂通道**（`CreateInstance<T>`）并入 C3/C5/C6 的构造计数：本批的根触发正落在这条通道上，漏掉它等于给三个清单留后门。

## Alternatives considered

- **给 `AppSetup` 加 Build 后 holder（或让编排器收 `IServiceProvider` 当参数）**：前者把 step-2b 刚删掉的共享可变态请回来，后者是把 service-locator 换个名字——都只让账面好看。
- **C6 只盖 `Run()`、不盖排序器文件**：`new StartupNoticeService` 这类服务构造会留在编排面，闸替现状背书；只盖文件不盖 `Run()` 形状，排序器可再长出分支与域文本。
- **C6 用「语句形态白名单」判「只排序」**：正则白名单对链式解析、目标类型化赋值等合法写法误报面大，维护成本高于收益；改取「无控制流 + 无引号」两条可判据，其余交 R1 评审面。
- **把服务构造留编排面并 grandfather 进 `SEQUENCER_VALUE_TYPES`，或只把脚本构建抽成静态方法**：前者让白名单混入服务即失去判据；后者编排面仍持窗口/回调/语言三件套的接线细节。

## Consequences

- 买到的：编排器构造参数经容器解图（接线层只剩 Build 后应用产出一件）；顶栏脚本构建与启动期展示面域文本离编排面；排序器形状机器化（C6）；C3/C5/C6 的构造计数同时覆盖 `new` 与 DI 工厂两条通道（16/16 顶格含该通道的触发构造）。
- 付出的：`StartupUseCaseRegistration` 增两处装配；`SEQUENCER_VALUE_TYPES`/`ROOT_CONSTRUCTIBLE` 是 deliberate inventory（新增须动它 + ADR）；`CompositionRootSequenceTests` 的链与消费扫描面同步扩展（`host.Marker` 的消费点搬进注册处）。

## Testing

- `dotnet test` 全绿 0 警告：229 + 225 + 6 + 603 = **1063**（+0；纯结构变更）。
- 机械门禁 `--enforce` 全绿：`verify-compose-root`（C3 16/16、C6 零违例；`--self-test` 16 条 ok——C6 六条：Run 控制流/引号、零服务构造、阶段值白名单、同步桥、空集，另含 C5 双构造通道一条）、`verify-code-health`、`verify-registration-discipline`、`verify-code-conventions`。
- 源序契约同步：`CompositionRootSequenceTests` 两条链（根触发改 `ActivatorUtilities.CreateInstance<Bootstrap.StartupSequence>`；编排链加 `InstallCaptionBar()`、`StartupNoticeTask(supervisor)`）与阶段产出消费扫描面（+`StartupUseCaseRegistration.cs`）；零行为变更（纯搬家 + 接线形态），故无新增单测。**实机未验**：沿用母篇的全链实机挂账（与波 2 同例，由实机批次销账）。
