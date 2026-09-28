# Agent Note: 运行时监督迁 Core——IRuntimeHost 端口与首批单测

Status: implemented

Review: LIGHT/2026-09-28#3/R2=ok（单轮 0 Blocker 0 Suggestion）

## Problem

`RuntimeSupervisor`（监督策略：崩溃恢复/重启退避/残留锁死处置）住在 Presentation 且构造直连具体 `HarnessRuntimeHost`，**全仓零单测引用**——重启退避、恢复异常退避、锁死跳过三条策略路径无任何回归网；架构规范把「监督策略」明列 Core 内容，实现却在外层。

## Decision

1. **Core 新端口 `IRuntimeHost`**（`DeepSeek.Harness.Desktop.Core`）：只收敛监督器的消费集四成员——`StderrTail`/`TryDetectUnreapableResidue`/`RestartAsync`/`WaitForExitAsync`；不追求宿主全貌（StartAsync/Stop/RuntimeDescription 等仍由组合根与退出管道直接消费具体类型，不为本批扩面）。
2. **`RuntimeSupervisor` 整体迁 Core**（`DeepSeek.Harness.Desktop.Core`，文件随迁）：构造收 `IRuntimeHost`；语句序/分支/异常边界零变更。`HarnessRuntimeHost` 实现端口（成员签名本就吻合，仅补接口声明）。
3. **首批单测**（`Core.Tests/RuntimeSupervisorTests.cs`）：fake 宿主模拟「N 次退出信号后挂起」的子进程生命周期（生产语义：`WaitForExitAsync` 阻塞至真实退出，监督循环停驻不空转），驱动四条路径——正常恢复（记序断言恢复屏先于导航）、残留锁死跳过重启、重启无 URL 重试、恢复异常退避不崩。

## Alternatives considered

- **端口收全宿主交互面**（含 StartAsync/Stop/StderrTail 写侧）：组合根与退出管道仍直连具体类型，端口成员落空；扩面无测试收益。落败（MVP 判据：端口只在解锁测试或消除重复决策时加）。
- **监督器留 Presentation、只抽端口进 Core**：接口在 Core、消费方在外层，方向反了；规范明列监督策略属 Core。落败。
- **fake 宿主每次 WaitForExitAsync 立即完成**：监督循环空转（测试实证 116 万次导航），必须模拟「挂起」生命周期。落败。

## Consequences

- 监督策略四路径首次有回归网；后续监督策略改动只落 Core+tests（LIGHT 面）。
- `IRuntimeHost` 成员即监督器消费契约：扩端口成员 = 扩消费面，须随 ADR。

## Testing

- `dotnet test` 860/860 全绿（Core 170 = 166+4 新增）；build 0 警告；format 0 差异；code-health/code-conventions/compose-root 门禁绿。
- 门禁自证：tier 判定 LIGHT（src/tests 面，不触根/门禁判据/workflows），评审走 R2 单路。

## Related

- 总纲：`.plan/整改方案-三平台震荡后结构清理-2026-09-27.md` §4.1 表 #11、§4.2「dsh 进程生命周期」行。
- 规范：[architecture-standards.md](../../../../docs/architecture-standards.md) R3。
