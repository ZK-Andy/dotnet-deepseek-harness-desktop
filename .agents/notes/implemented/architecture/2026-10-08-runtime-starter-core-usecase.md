# Agent Note: IRuntimeStarter 先行——起步链下沉 Core 用例

Status: implemented

Review: FULL/2026-10-08/R1=ok R2=ok R3=ok

方案篇见 [composition-mechanism-container-assembly](../../proposed/architecture/2026-10-08-composition-mechanism-container-assembly.md)（容器组装对象图四步，本篇只落第一步"先行 `IRuntimeStarter`"）。

## Problem

`StartupSequence.StartRuntime` 是启动链里最大的一块不可测：shim 注册 → 宿主 spawn 等 URL（`GetAwaiter().GetResult()` 阻塞）→ 三行留痕 → 壳铸币，全钉死真实 dsh 进程与 `_shellForward`/`_timeouts` 直引。行为证明只能靠整机实机，fake 盖不住分支（引导态跳过、无 URL 降级、铸币失败仍开窗）。

## Decision

- 新端口 `Core/Bootstrap/IRuntimeStarter.cs`（`Task<DshWebUrl?> StartAsync(IRuntimeHost, CT)`）+ 新用例 `Core/Bootstrap/RuntimeStarter.cs`（64 行）：原体逐句下沉，`IsNeeded` 门控、日志三行、`TakeLast(8)`、铸币失败仍回端点逐字等价；两处阻塞改 `await + ConfigureAwait(false)`，`ct` 贯通 spawn 与铸币。
- `IRuntimeHost` 收敛两成员（`StartAsync`、`RuntimeDescription`，`HarnessRuntimeHost` 已有实现，纯端口补齐）。
- `StartupSequence` 只收一个可选构造参数（缺省以既有字段组装，根调用点零改——`verify-compose-root` 16/16 守恒）；`StartRuntime` 缩为委托一行，方法名保留（`CompositionRootSequenceTests` 链不断）。
- 防线：新 `Core.Tests/Bootstrap/RuntimeStarterTests.cs` 4 例（引导态跳过且 shim 仍执行 / 无 URL 不铸币且 stderr 取后 8 行 / 超时透传 + 铸币记序 / 铸币失败仍回端点）；`RuntimeSupervisorTests` 两 fake 补端口成员（语义守恒）；全绿 1035（209 + 223 + 603），基线同步 1031 → 1035，覆盖率维持 63.59%；四闸 `--enforce` 全绿（compose 16/16、registration 1/1 与 15/15）。

## Alternatives considered

- **实现进 Infrastructure（落败，实测）**：首版曾落子 Infrastructure，`StartupSequence` 内 `new RuntimeStarter` 即触发 `verify-registration-discipline` R1a（infra direct-new 2 > 1，`--enforce` 红）。用例经委托注入后零 Infrastructure 引用，住 Core（`UpdateCoordinator` 先例）同时消掉违规，且更靠近方案篇"真正的组合在 Core"。
- **本批一并把 `IRuntimeStarter` 装进容器（递延，step-2/3 的事）**：其依赖（bootstrap 实例、`_shellForward` 单例、spawn 超时、`HostLog.Write`）全在容器外，强行装配等于提前删 locator；本批保持根零改，装配随 locator 删除走。
- **Run 链整体 async 化一次到位（递延）**：`GetAwaiter` 消除涉及 Ryn `Run` 线程模型审查（方案篇 Consequences 已标风险），且 `IStartupSequence.Run` 签名涟漪到根与契约测试；本批只把 `await` 备好，阻塞点收敛到 `StartRuntime` 一行并注释锚定。
- **无接口、具体类直注（落败）**：方案篇 Proposal 点名 `IRuntimeStarter` 接缝；`StartupSequence` 可选注入 fake 是本批可测性的落点。与 `UpdateCoordinator` 无接口不矛盾：后者的端口是其依赖，前者的端口是 Starter 自身（R1 复核结论）。

## Consequences

- 买到的：起步链可单测；`IsNeeded` 双判读经 R2 确认为稳定字段快照语义，无新竞态；门禁从"尺寸数数"向"方向断言"走出第一步（新增构造全部落在 Core/IRuntimeHost 端口之后）。
- 付出的与接受项：生产缺省组装分支（`?? new RuntimeStarter`）零执行覆盖——构造签名 + 方法组转换由编译器钉死，风险低，覆盖随 step-2 容器装配自然到来（R2 Suggestion 裁定接受）；三套 `IRuntimeHost` fake 暂不收敛（分属监督/起步不同语义，强并会耦合测试装置，R1 Suggestion 裁定驳回）。
- 剩余：方案篇 2–4 步（locator 删除、`IOptions` 化 + 接线层改名、闸改"只引用端口 + Options + 适配器"）未动，`HANDOFF-todos` 首条保持 `[ ]` 跟踪。

## Testing

- `dotnet test` 全绿 0 警告：209（Core，含新 4 例）+ 223（Tests，含组合根序列链）+ 603（Infrastructure）。
- 行为级回归：`CompositionRootSequenceTests` 三链（根前头部序、编排主链序、阶段产出有消费）全绿；新增 4 例为本批行为钉子。
