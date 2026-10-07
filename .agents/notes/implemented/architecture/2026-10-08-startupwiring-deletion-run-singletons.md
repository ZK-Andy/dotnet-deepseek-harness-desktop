# Agent Note: 接线槽删除——8 槽位转容器单例与值流

Status: implemented

Review: FULL/2026-10-08/R1=ok R2=ok R3=ok

方案篇见 [composition-mechanism-container-assembly](../../proposed/architecture/2026-10-08-composition-mechanism-container-assembly.md)（容器组装对象图四步，本篇只落第二步"再删 locator"；第一步见 [runtime-starter-core-usecase](2026-10-08-runtime-starter-core-usecase.md)）。

## Problem

`StartupWiring` 是手搓定位器：8 个槽位（App/WindowAccessor/Host/SupervisorCts/Tray/Exit/HealthMonitor/LastRecoveryShownAtUtc）跨"注册早于赋值"时序由可变共享包桥接，每加一个功能加一个槽位（`verify-compose-root` C3 16/16 顶格的结构根因之一）。槽位分三类寿命（装配期就绪 / Run 期创建 / 编排器内部），却共用一种机制。

## Decision

- 槽位映射（行为零变化，创建时序逐个等价）：
  - App/WindowAccessor → 方法局部 + `AppSetup` 值直传（post-Build 值，无单例化必要）。
  - Host + marker → `RunServicesRegistration.AddRunHost` 惰性单例（`HostSetup` struct 用 Type 重载注册；首次解析在编排 Run 期）。
  - SupervisorCts → 根方法局部 + `AddSingleton` 实例（寿命 = 本次 Run，释放随作用域）。
  - Tray → `AddSingleton` 实例（装配期构造，`ConfigureIcon` 时序不变）。
  - Exit → `AddExitPipeline` 惰性单例（`Close` 动作调用期经容器解析；once-guard 单实例语义不变）。
  - HealthMonitor → `AddHealthMonitor` 惰性单例（`CurrentWindowAccessor` 调用点解析）。
  - LastRecoveryShownAtUtc → 序列私有字段（纯内部往返）。
- 路由闭包改调用期经容器解析（`RecoveryRegistration` 已有同型先例）；单实例回调 pre-Build 空窗改 `builtApp` 局部守卫（窗口期 outcome 等价，R2 实证）。
- 序列改根显式组装（容器只给零件），`IStartupSequence` 注册删除、端口保留（step-3 重议存废，见 Consequences）。
- 释放上移：宿主释放随根 finally（原序列 finally 语义），CTS 随 using 作用域（晚于 proxy 释放，无交互，R2 实证等价）。
- 门禁读数：C3 16 → 14（序列工厂与 `AppSetup` 工厂两 `new` 消除），R1a 1/1、R1b 15 → 11（marker 两静态点迁入注册文件），F4 双双回阈值内（`Run`/序列组装调用打包、`BuildApp` 签名收单行）。

## Alternatives considered

- **Host/CTS/Exit 继续放编排器私有字段、只删包不转容器（落败）**：字段方案对 Host/CTS 可行，但 Exit/Health 的调用方（命令路由）在注册期就需要可引用的源——字段不可达，只能再造小包，等于改名续命。容器单例是唯一同时满足"注册期可引用、Run 期创建、调用期求值"的机制。
- **Exit `Close` 动作随单例提前绑定窗口（落败）**：注册期窗口不存在；提前绑定只能传空委托再二次赋值（又一个槽位）。调用期经容器解析与 `RecoveryRegistration` 同型，时点必在窗口就绪之后。
- **`IStartupSequence` 端口随注册一并删除（落败，step-3 遗产）**：端口零注册/零解析已成事实，但 step-3 闸改向可能重新以它为断言面；删定义是单行事，留待 step-3 与改名一批定夺，注释已留重议锚（R1/R3 会师结论）。
- **Host/CTS 释放留在序列 finally（落败）**：寿命 owner 已是组合根（构造注入/using），释放权跟随 owner 是 R1 原文"只做装配、启动、接线与兜底"的自然归属；序列不再拥有任何寿命。

## Consequences

- 买到的：可变共享包清零（`StartupWiring.cs` 删除，全仓零残留引用）；跨阶段值只经运行期单例（调用点幂等解析）与方法局部流动；C3/R1b 读数下降为 2b 留出余量。
- 付出的：序列内 5 处调用点 `GetRequiredService` 解析（幂等单例，无副作用；收字段反而增状态面，R1 裁定保持）；根 `Run` 仍 50 余行（2b 根字段归零后再缩）。
- 剩余：2b（根 `_timeouts/_captionBar/_uiLocale/_shellForward` 进容器，`Run` 缩到触发）与 step-3（接线层改名 + 闸改"只引用端口 + Options + 适配器"）未动。
- 与 form-separation 的关系：该篇 Decision 2 原文"终结的是『编排器住在组合根』，不是『编排器由容器驱动』"——本批"根显式组装 + 容器只给零件"与其同构，属允许的细化，非决定偏离，无 Erratum 义务。

## Testing

- `dotnet test` 全绿 0 警告：209 + 223 + 603 = 1035（计数不变，基线不动）。
- `CompositionRootSequenceTests` 三链锚同步（容器前头部/编排主链/恢复打点；新增 host-before-spawn 锚 `GetRequiredService<HostSetup>()`，R2S2）；值流测试（阶段产出有消费）未动全绿。
- `dotnet format --verify-no-changes` 过；四闸 `--enforce` 全绿（compose 14/16、registration 1/1 与 11/15）。
