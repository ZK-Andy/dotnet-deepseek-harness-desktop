# Agent Note: 排序器阶段值退役——监督任务以局部流转，C6 白名单空集

Status: implemented

Review: FULL/2026-10-15#4/R1=ok R2=ok R3=ok

Erratum: 2026-10-15 — Testing 的「C3 16/16」读数已由 [sequencer-resolution-injection](2026-10-15-sequencer-resolution-injection.md) 的 C3 压舱降为 **6/6**（帽 16→6），同批 `--self-test` 夹具由 **17 增至 19**（C7「装配期构造清单」两条）；本篇记的是当时读数，判据面结论不变。

Erratum: 2026-10-15 — 正文点名的 `Run()`/`RunAppLoop(Task)` 已随两相入口退役：入口收成 `RunStartupAsync`/`RunMainLoop`，主循环承载与收口顺序搬 `Core.Bootstrap.AppExitOrchestration`（见 [entry-bridge-two-phase](2026-10-15-entry-bridge-two-phase.md) 与 [app-exit-orchestration-core-usecase](2026-10-15-app-exit-orchestration-core-usecase.md)）；「监督任务以局部 `Task` 流转、不设阶段产出」的结论不变。

组合根残留批的波 A。母篇见 [composition-mechanism-container-assembly](2026-10-08-composition-mechanism-container-assembly.md)，前序两篇见 [startup-usecases-sink-to-core](2026-10-15-startup-usecases-sink-to-core.md) 与 [startup-sequencer-shape](2026-10-15-startup-sequencer-shape.md)。

## Problem

step-1 收官后排序器只剩一处自有类型构造：`SetupSupervisor` 把容器单例取消令牌源与监督任务打包成 `new SupervisorSetup(cts, task)`。为它，C6 留了一条白名单（`SEQUENCER_VALUE_TYPES = { "SupervisorSetup" }`）——判据从「排序器零构造」退成「白名单内构造」：白名单一旦非空，任何服务构造都能靠改集合缝合，闸不再能回答「排序器有没有长出装配」。

同处还有两笔账：取消令牌源是容器单例（`RegisterServices` 已 `AddSingleton(supervisorCts)`），排序器却按 service-locator 解析；打包出来的一对值，健康观测与启动期告知只消费令牌、退出编排只消费任务——record 存在的意义只剩「把两个局部值一起搬」。

## Decision

- **`SupervisorSetup` 退役**：监督任务以局部 `Task` 在 `Run()` 内流转，只有真正消费它的退出编排收参（`RunAppLoop(Task)`）；健康观测与告知不再收该参数（它们只消费令牌）。
- **取消令牌源改构造注入**：`StartupSequence` 构造收 `CancellationTokenSource`（容器单例，接线层零改动——登记为 `DesktopAdapter.RegisterServices` 的 `services.AddSingleton(supervisorCts)`，`DesktopAdapter.App.cs:153`），`SetupSupervisorAsync`/`SetupHealthMonitor`/`StartupNoticeTask` 经字段取令牌；解析时点由方法体内前移到构造，同一实例单例、无副作用面。
- **更名 `SetupSupervisorAsync`**：返回 awaitable，按 D001/VSTHRD200 加 `Async` 尾缀。
- **C6 判据收紧为空集**：`SEQUENCER_VALUE_TYPES = set()`（重开须另写 ADR），docstring 改为按实际判据表述（两条具名通道 + 可归因的 target-typed `new()`）；排序器内**不可归因**的 target-typed `new()` 与根侧 C3 同待遇判红（原先该兜底只扫根文件）。self-test 仍 17 夹具：新增「白名单必须为空」，原「阶段值白名单」夹具改为「unattributable target-typed `new()`」——不再与「服务构造」夹具重复覆盖同一判据。
- **源序契约同步**：`CompositionRootSequenceTests` 链名随更名，`StageOutputs_CarryConsumedValues` 的产出集去掉 `SupervisorSetup`。

## Alternatives considered

- **保留 `SupervisorSetup`，把构造搬进注册工厂（容器解图）**：类型留着只为一对局部值，白名单随之长期非空——C6 的「零构造」判据退化成维护白名单，正是本项要治的形态。落败。
- **只把令牌源改构造注入、保留 record**：排序器仍有 `new`，白名单清不掉，「收紧为空集」落不了地。落败。
- **健康观测/告知继续收 `Task` 参数以保留值流形状**：二者不消费该任务，纯废参数；「阶段产出即执行序证明」的价值已由源序测试承担。落败。
- **用元组 `(CancellationTokenSource, Task)` 流转替代 record**：令牌源已由容器供给，元组只剩单元素意义；打包本身没有消费者。落败。

## Consequences

- 买到的：排序器**零自有类型构造**，C6 白名单空集（重开须再写 ADR）；Cts 不再经 service-locator 解析；一个只为搬运而存在的阶段值类型与其打包/解包一起消失。
- 付出的：`StartupSequence` 构造多一个参数（容器解图，接线层零改动）；`RunAppLoop` 形参改名（字面进入源序测试契约，改名须同步）；闸自测夹具随判据收紧改写。

## Testing

- `dotnet test` 全绿 0 警告：**1063**（+0；纯结构变更，无新增单测）。
- 机械门禁 `--enforce` 全绿：`verify-compose-root`（C3 16/16、C6 零违例、白名单空集；`--self-test` 17 夹具）、`verify-registration-discipline`、`verify-code-conventions`。
- 源序契约同步：`CompositionRootSequenceTests` 两条（编排链名、产出消费扫描面收窄到四个阶段产出）。**实机验收 ✅（2026-10-15，随 v0.6.9 装机）**：零行为变更面随母篇同批取证（`~/.dsh/logs/host.log` 07:25 起全链健康）。
