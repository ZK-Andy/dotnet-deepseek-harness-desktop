# Agent Note: 根字段归零——装配期共享态进容器单例

Status: implemented

Review: FULL/2026-10-08/R1=ok R2=ok R3=ok

方案篇见 [composition-mechanism-container-assembly](../../proposed/architecture/2026-10-08-composition-mechanism-container-assembly.md)（容器组装对象图四步，本篇只落第二步之二"根字段归零"；step-2a 接线槽删除见 [startupwiring-deletion-run-singletons](2026-10-08-startupwiring-deletion-run-singletons.md)，先行 `IRuntimeStarter` 见 [runtime-starter-core-usecase](2026-10-08-runtime-starter-core-usecase.md)）。

## Problem

step-2a 之后组合根仍持有四个字段（`_timeouts/_captionBar/_uiLocale/_shellForward`，`DesktopBootstrap.cs:15-25`）：每个新功能加一个字段、加一个 `StartupSequence` 构造参数——`verify-compose-root` C3 14/16 的剩余结构成因。四个字段分两类寿命（一次性配置读值 / 跨阶段共享单例），却共用"字段直传"一种机制；且 `Run` 在 HEAD 已 60/60 顶格 F4，任何增量都须先拆。

## Decision

- 字段清零：四个字段删除，本类零字段（无状态接线层）；`ResolveSharedState()` 一次创建（元组返回），`Run` 方法局部解构——前 Build 消费走局部（单实例仲裁超时、`BuildApp` 选项、托盘/更新栈、代理装配），编排期消费经容器回读（同一实例）。
- 进容器：`RunServicesRegistration.AddBootstrapSharedState` 登记四个实例单例（`RegisterServices` 内与 `AddRunHost` 同列）；编排组装经 `container.GetRequiredService<T>()` 回读——单实例唯一，`UiLocale.Changed` 订阅与铸币态共享同一身份。
- 时序等价：`UiLocale` 创建点从仲裁内提至 `Run` 头（早于 `ResolveRuntimeAndDev`）——`isEnglishProvider` 闭包求值点仍在引导任务启动时（晚于创建），与原"字段延迟赋值、调用点晚于赋值"语义等价；`RuntimeTimeouts/CaptionBarOptions.Load` fail-safe 语义逐字不变；`DshShellForward` 构造无 I/O，前后一致。
- 方法清单：`ResolveSharedState` 进 C2 装配清单（装配级新增按闸注释走 deliberate 通道）；`ResolveRuntimeAndDev/AcquireSingleInstance/StartProxy` 转 `static`（无状态的机器可查证明）。
- 闸正则：`_COMPOSE_METHOD_RE` 容忍元组返回类型（`(?:static\s+)?(?:\([^;{}]*\)\s+)?`），否则 C2 把 `ResolveSharedState` 误判为成员 `static`；自测加元组用例锁定。
- 测试锚：`CompositionRootSequenceTests` 前头部链 `StartProxy();` → `StartProxy(shellForward);`（调用点带参、尾分号钉死调用而非定义，序不变）。
- F4 收口：base `Run`/`BuildApp` 均为 58（F4 60 顶格，2a 已证回阈值内）；朴素穿参曾把两者顶到 79/72（F4 红），本批以助手抽出 + 组装收拢 + 单行调用压回 `Run` 57、`BuildApp` 58（净 Δ −1/0）。

## Alternatives considered

- **共享态包成新 struct 经阶段产出传递（落败）**：新增 `SharedState` 纪录看起来能收签名行，但它是 `StartupWiring` 换皮——可变共享包复活；元组只活在 `Run` 方法内、前 Build 边界一次性解构，post-Build 统一走容器，无跨阶段包。
- **只转方法局部、不进容器（落败）**：`Run` 局部 + 显式传参即达字段清零，但四个单例登记零读者——R1 会判死登记；容器回读让登记即被消费（组装点四处解析），单实例身份可证伪（换实例即行为变）。
- **`StartupSequence` 改从容器内自解析、删四个构造参数（递延，step-3 的事）**：那才是"容器组装对象图"的终态，但涉及四个分部文件的字段改属性 + 恢复面测试锚（`_shellForward.InvalidateRoute()`）迁移；本批保持序列签名零改，diff 只在根与注册。
- **母篇"IOptions 化"去向（本批已覆盖，非偏离）**：母篇 step-2 原文"`CaptionBarOptions/RuntimeTimeouts` 经 `IOptions<T>` 消费（`LaunchOptions` 已是样板）"——样板 `LaunchOptions` 本人即"解析一次 + 构造注入"而非 `Microsoft.Extensions.Options.IOptions<T>`；2b 与之同构（`Run` 头一次 `Load` + `AddSingleton` 实例注入 + 构造/解析消费），故不另做 `IOptions<T>` 包装。若后人要真 `IOptions<T>`，属 step-3 闸改向时一并议，不在本批。
- **`IRuntimeStarter` 本批一并容器装配（递延，step-3 的事；时点注记）**：其 `bootstrap` 依赖来自 `Preflight`（不在容器），强装等于提前搬 `Preflight` 进容器；缺省组装分支保持。先行篇原文是"装配随 locator 删除走"（即 2a），2a 未做、时点已漂到 step-3，特此注记漂移（非先行篇已裁定）。

## Consequences

- 买到的：根字段 4 → 0（`grep '^    private.*_;' DesktopBootstrap*.cs` 零命中）；新功能不再加根字段；三个根方法 `static` 化后"无状态"由编译器钉死；C3 14 → 13（字段目标类型 `new()` 消除），R1a 1/1、R1b 11/15 不动。
- 付出的：`BuildApp/RegisterServices` 签名各 +4 参数（单行长签名；终态随 step-3 容器自组装收回）；C2 清单 +1（`ResolveSharedState`， deliberate 通道 + 本 ADR）。
- 剩余：step-3（接线层改名 + 闸改"只引用端口 + Options + 适配器"，含序列自解析/`IRuntimeStarter` 容器装配的终态）未动；`HANDOFF-todos` 首条保持 `[ ]` 跟踪（2b 勾销、step-3 续行）。

## Testing

- `dotnet test` 全绿 0 警告：209 + 223 + 603 = 1035（计数不变，基线不动）。
- `CompositionRootSequenceTests` 六例全绿（含更新后的 `StartProxy(shellForward);` 锚；三链序/值流/门控锚未动）。
- `dotnet format --verify-no-changes` 过；`verify-compose-root --self-test` 过（含新元组用例）；五闸 `--enforce` 全绿（compose 13/16、registration 1/1 与 11/15、health OK）。
