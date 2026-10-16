# Agent Note: 退出编排下沉 Core 用例（RunAppLoop 从排序器退场）

Status: implemented

Review: FULL/2026-10-15#6/R1=ok R2=ok R3=ok

Related: 排序器形状 [startup-sequencer-shape](2026-10-15-startup-sequencer-shape.md)；用例下沉同族 [startup-usecases-sink-to-core](2026-10-15-startup-usecases-sink-to-core.md)；退出管道寿命与幂等 [composition-root-value-flow-pipeline](2026-09-15-composition-root-value-flow-pipeline.md)；承接篇 [entry-bridge-two-phase](2026-10-15-entry-bridge-two-phase.md)。

## Problem

`StartupSequence.RunAppLoop` 是排序器里剩下的最后一段**业务编排**：主循环的 try/catch、留痕两行、取消监督器与首启引导、按预算 join 监督任务并吞 `AggregateException`、惰性解析退出管道回收运行时、释放单实例监听器、返回退出码。这与 C6 的「排序器只按序触发用例」形状相抵：异常边界与编排序（回收先于监听器释放）都在排序器内，测试面为零（排序器不在任何测试工程的引用面内，`RunAppLoop` 无 caller 三跳内的测试）。同时它是「`await` 贯通」的前置——相位切分要先让收口顺序有一个可单测的家。

## Decision

1. **新 Core 用例 `Core.Bootstrap.AppExitOrchestration`**：逐句承接原 `RunAppLoop` 的全部语义（留痕文案、异常吞并边界、取消序、join 预算、回收序、退出码恒 0），构造注入容器单例（监督器令牌源、首启引导端口）、**join 预算按配置模型投影成 `TimeSpan`**（Core 不引 Infrastructure 的 `RuntimeTimeouts`，与 `RuntimeStarter` 的 spawn 预算同口径）与日志回调。
2. **一次运行的三笔运行期值走调用参数**：主循环委托、监督任务、回收动作、监听器释放动作。回收动作保持**惰性**（`() => _resolutions.ExitPipeline.Value.ReapRuntime()`）——退出管道经宿主派生，首次解析点仍在收口点，与 [sequencer-resolution-injection](2026-10-15-sequencer-resolution-injection.md) 的时点承诺一致。
3. **thread 0 约束写进用例契约**：主循环经 `RynApplication.Run()` 阻塞，worker/pool 线程调用直接抛（Ryn 线程模型），故本用例的调用点只能落在同步相（见承接篇 [entry-bridge-two-phase](2026-10-15-entry-bridge-two-phase.md)）。
4. **排序器只触发**：`RunAppLoop` 收成一行 `_exit.Run(...)`；C6 的形状面不因本批新增任何构造（`SEQUENCER_VALUE_TYPES` 仍空集）。

## Alternatives considered

- **留在排序器里（落败）**：编排序与异常边界继续住在形状闸只做「无分支/无域文本」约束的地方，且永久不可单测；C6 的形状契约与实现现实持续相抵。
- **并入 `ExitPipeline`（落败）**：`ExitPipeline` 是跨路径共用的幂等管道（托盘有序退出 / 重启 / 自更新兜底强退），寿命与语义都是「退出动作集」；主循环承载与监督任务 join 属一次 Run 的编排，合并会把 run 期编排塞进共用管道，且它的实例在收口点才惰性解析，拿不到监督任务。
- **把 join 预算收成 Core 侧 options 记录（落败）**：只有一项数值，另立记录等于给同一事实开第二个家（配置模型 `RuntimeTimeouts` 已是唯一家）；投影成 `TimeSpan` 与起步用例同形。
- **把主循环委托也改为容器里的委托注册（落败）**：主循环是 Build 后产出（`AppSetup.App`），容器拿不到；排序器经调用参数传入是既有时点约束下的唯一路径。

## Consequences

- 买到的：收口顺序（取消 → join → 回收 → 释放监听器）与异常吞并边界有了单测家（主循环正常/抛异常/监督任务故障/join 预算用尽四路）；排序器的「只触发」形状从语句面延伸到一行调用。
- 付出的：排序器构造表多一项（`AppExitOrchestration`），收口动作以两个委托跨层传递（回收惰性、监听器释放直接取方法组）；端口 `IStartupSequence` 同批两相化为 `RunStartupAsync`/`RunMainLoop`（见 [entry-bridge-two-phase](2026-10-15-entry-bridge-two-phase.md)）。
- 边界：join 预算的**数值**仍由配置模型持有（`SupervisorJoinTimeoutSeconds`），用例只消费投影值；退出码语义不变（恒 0）。
- **实机验收 ✅（2026-10-15，随 v0.6.9 装机）**：收口/回收链与两相切换的线程行为在真实桌面走通——`~/.dsh/logs/host.log` 有页面健康行、无连续导航/提交超时降级、恢复后窗口回代理根；收养路径逐 pid 收割（「收割桌面运行时残留」）即收口序的实机证据。

## Testing

- 新增 `tests/DeepSeek.Harness.Desktop.Core.Tests/Bootstrap/AppExitOrchestrationTests.cs` 四用例：正常路径断言记序（主循环 → 取消监督器 → 取消引导 → 回收 → 释放监听器）与引导取消次数；主循环抛异常断言吞并 + 留痕（含异常文案）+ 收口照走；监督任务已故障断言 `AggregateException` 被吞且回收照走；监督任务超预算（50ms）断言放弃等待即收口、监听器为 null 不炸。全绿（4/4）。
- `dotnet build` 0 错误（`--no-restore`；NU1900 为沙箱下 NuGet 漏洞数据缓存只读的环境警告，与本批无关）。
- 源序契约测试 `CompositionRootSequenceTests` 的链序随之更新（承接篇 [entry-bridge-two-phase](2026-10-15-entry-bridge-two-phase.md) 一并落）。
