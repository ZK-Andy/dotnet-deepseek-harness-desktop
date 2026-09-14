# Agent Note: value-flow-batch2-value-pipeline（值流管线批次 2：阶段产出值流）

Status: implemented

Review: FULL/2026-09-15/R1=ok R2=ok R3=ok

Superseded（部分）：`Preflight` 产出的两个裸 bool（`IsDev`/`DevAutoIsolated`）由 [2026-09-15-value-flow-batch3-finalization](2026-09-15-value-flow-batch3-finalization.md) 收为一个类型化 `LaunchOptions`；本 ADR 其余阶段产出结论仍有效。

## Problem

[composition-root-value-flow-pipeline](2026-09-15-composition-root-value-flow-pipeline.md)（implemented，四批次主 ADR）批次 2：批次 1 把 C 类状态下沉真类型服务后，组合根 `Run()` 主链仍以 6 个**空载荷** `record struct` token（Preflight/Host/Runtime/Update/App/Supervisor）串联——token 只承载"上一阶段已执行"的类型承诺，实际产出（webUrl、host、marker、app、windowAccessor、监督器与退出令牌）仍落字段。排序不变量只活在 token 类型与注释里：消费段整段漏调、乱序仍可编译（唯一硬约束是 UpdateToken 被 BuildApp 消费）。本笔记承载批次 2 的执行定案。本批部分取代 [composition-root-stage-typing](2026-09-03-composition-root-stage-typing.md) 的空载荷 token 形态——偏序机制保留，token 载荷由空升格为真实产出。

## Decision

1. **token 升格为类型化阶段产出**：6 个空载荷 token 删除，代之以 6 个私有嵌套 `record struct`，各自携带本阶段真实产出——`Preflight(IFirstBootBootstrap Bootstrap, bool IsDev, bool DevAutoIsolated)`、`HostSetup(HarnessRuntimeHost Host, RunMarkerResult Marker)`、`RuntimeSetup(DshWebUrl? WebUrl)`、`UpdateSetup(UpdateCoordinator Updates)`、`AppSetup(RynApplication App, CurrentWindowAccessor WindowAccessor)`、`SupervisorSetup(CancellationTokenSource Cts, Task Task)`。产出记录的每个属性都必须有消费段真实读取（监督器实例只在 `SetupSupervisor` 内部被 `RunAsync` 消费，不作产出载荷）。阶段方法返回产出、消费段收参数；产出即执行序证明——绕过产生阶段即缺值编译失败（实测：删 `StartRuntime` 产出后 `InitCloseGateAndUpdateStack`/`BuildApp` 报 CS0103 `runtime`）。
2. **字段随产出流动收缩**：`_bootstrap/_isDev/_devAutoIsolated/_marker/_updates/_supervisor/_supervisorTask/_supervisorCts` 8 个字段删除（18→10），其值经阶段产出传递；退出令牌只剩 `_supervisorCtsRef` 一处持有（删掉与其同点赋同一实例的 `_supervisorCts` 镜像，Run 尾部改释放该唯一引用）。留字段只剩两类——早于赋值的惰性捕获点（`_host/_app/_windowAccessor` 在服务/控制器构造期即被 `() =>` 委托读取）与赋值后仍被异步回调改写/读取或跨阶段延迟接线（`_webUrl` 导航靶点、`_supervisorCtsRef` 退出令牌、`_exit` 托盘路由、`_healthMonitor` 诊断快照、`_tray` 二次启动回调、`_instanceListener`、`_uiLocale` 语言单点）。
3. **`DshWebUrl` branded 值进 Core**（拍板 2「跨 R3 边界的值 branded 进 Core」）：dsh web 端点以 `readonly record struct` 表达，引导端口 `IFirstBootBootstrap.Start` 的回调签名由 `Func<Uri,…>` 改为 `Func<DshWebUrl,…>`，实现侧 `FirstBootBootstrapService` 在交付处 `DshWebUrl.From`；空实例（`default`/无参构造）访问 `Value`/`Authority`/`ToString` 以具名 `InvalidOperationException` fail loud，堵住 `Uri?` 转非空标注留下的 NRE 洞。壳侧 `_webUrl` 字段仍持裸 `Uri`（Presentation 内部导航靶点，非跨界值）。`HarnessRuntimeHost.StartAsync` 保持 `Uri?`（宿主内部契约不动，避免为品牌化扩面）。
4. **顺序契约参数**：三个消费段只依赖顺序、不消费载荷（`SetupHostAndMarker` 的 `Preflight`——宿主创建读的 DSH_HOME 由 `ResolveRuntimeAndDev` 设置；`InstallCompanionBeforeSpawn` 的 `HostSetup`；`InitCloseGateAndUpdateStack` 的 `RuntimeSetup`），保留参数并标明"顺序契约参数"——这是原 token 偏序承诺的等价物，删参即退回源码语句序。消费段本身不消费的载荷由后续段消费（如 `RuntimeSetup.WebUrl` 由 `BuildApp` 消费落位）。
5. **记序网随形态更新**：`CompositionRootSequenceTests.RunChain_StageCalls_AreInContractOrder` 断言新主链调用串；新增 `StageOutputs_CarryConsumedValues` 钉"空载荷 token 形态退役 + 六个产出类型皆带载荷 + **每个产出属性至少有一处消费点**"（死载荷回归网；消费锚定到阶段产出变量 `preflight/host/runtime/update/app/supervisor`，避免无关同名成员如 `arrived.Task` 误判为已消费），其参数表按顶层逗号切分（容忍元组/泛型实参）。`DshWebUrlTests`（Core）覆盖工厂构造/origin 派生/空实例 fail loud。`SettleHandle_CreatedBeforeBootstrapTaskStarts` 不变。

## Consequences

- `dotnet build -warnaserror` 0 警告；`dotnet test` 628/628（基线 624 + 新增 4：值流形态网 1 + `DshWebUrl` 契约 3）；`dotnet format` 与机械化门禁全绿。
- 零行为变更自证：无 env 注入变化 / 无子进程 spawn 形态变化 / 无可观察副作用变化；`_webUrl` 落位点由 `StartRuntime` 移到 `BuildApp`（两者之间无读取点，取值同源）；`RegisterServices` 由方法组改闭包传参、`RunBootstrapIfNeeded` 回调由方法组改捕获 `AppSetup` 的 lambda，调用序与实参逐一等价；`WireExitHandlers` 的 `supervisorCts.Cancel` 与原 `() => _supervisorCts.Cancel()` 同一实例。
- 编译期依赖序以临时抽段验证（删 `StartRuntime` 产出 → `InitCloseGateAndUpdateStack`/`BuildApp` 报 CS0103 两处 → 还原）；残余偏序边界与批次 0 一致：真并行 fan-out（健康/更新检查/横幅）无序，类型填不动它是正确的。
- A 类启动配置（`IsDev/DevAutoIsolated`）本批随 `Preflight` 值流化，批次 3 的 IOptions 化对象随之从字段改为该产出的配置来源，不构成返工。
- 语言下界残差：阶段 exactly-once 与异步竞态仍不可编译期证明（ADR Risks 已列），归宿记序/并发测试，本批不新建并发网。
- 门禁面：A5 嵌套判据已于 B4 退役（`official-clean-architecture-adoption`），无判据需随新类型形态更新；D005 主工程豁免面无新增。

## Alternatives considered

- **保留空载荷 token、另加值参数**：同一阶段既收 token 又收值，重载同一不变量两遍，且 token 仍是可绕过的空壳。落败。
- **单一 `BootstrapContext` 累积所有阶段产出**：主 ADR Alternatives 已否决（不消除耦合、加寻址层）；本批的六个产出各属其产生阶段、载荷互不重叠，与"把字段分组打包在阶段间传 context"不同构。落败。
- **把 `_webUrl` 迁入长命服务（导航靶点服务）**：能消除字段，但 webUrl 的排序不变量正来自"阶段产出"语义（计划验收以 webUrl 为例）；迁服务会把它从值流里摘出去，反而弱化本批目标。留字段（Presentation 内部靶点）+ 阶段产出传递。落败。
- **消费段一律只读字段、产出仅作顺序标记**：等于 token 改名，编译期仍不检查"缺前置产出"，验收不成立。落败。
- **`HarnessRuntimeHost.StartAsync` 直接返回 `DshWebUrl?`**：品牌化最彻底，但改宿主公共 API 会牵动 Infrastructure 内部 attempt/handoff 与既有测试，超出批次 2 值流面。落败——品牌化落在 R3 端口边界（`IFirstBootBootstrap.Start`），宿主内部保持 `Uri?`。
