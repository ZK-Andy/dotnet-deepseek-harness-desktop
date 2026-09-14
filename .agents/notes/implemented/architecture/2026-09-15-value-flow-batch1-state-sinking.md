# Agent Note: value-flow-batch1-state-sinking（值流管线批次 1：状态下沉）

Status: implemented

Review: FULL/2026-09-15/R1=ok R2=ok R3=ok

Superseded（部分）：组合根实例字段数由批次 2 续降至 10（[2026-09-15-value-flow-batch2-value-pipeline](2026-09-15-value-flow-batch2-value-pipeline.md)）；本 ADR 的 C 类状态下沉结论仍有效。

## Problem

[composition-root-value-flow-pipeline](../../proposed/architecture/2026-09-15-composition-root-value-flow-pipeline.md)（proposed，四批次主 ADR）批次 1：批次 0 定稿的拆分线（实施计划 §5）要求把 C 类跨线程共享态从组合根字段迁入真类型服务——托盘行为、更新栈、引导任务、退出编排——并把闭包喂容器的注册面改为构造注入。此前这些字段与后台 Task 闭包、事件委托、DI 回调互锁在组合根内，不变量只靠注释与赋值时点维持。本笔记承载批次 1 的执行定案；主 ADR 保持 proposed（批次 3 才迁）。

## Decision

1. **托盘控制器** `Services/Tray/TrayController`：`_maximizedAtHide/_trayReady/_trayAvailable` 与 `HideForTrayAsync/RecallAsync`、`Closing` 拦截订阅、语言切换菜单重建迁入；`ShowTray` 留装配壳（解析 Ryn `TrayService` 交控制器，解析与 Show/SetMenu 同在控制器 try 内）。launcher 激活回调经 `ActivateFromLauncherAsync` 共用最大化样本消费契约。关窗闸门/偏好经 `CloseGate`/`CloseBehavior` 访问器供路由构造注入。
2. **更新协调器** `Services/Update/UpdateCoordinator`：`_updateMachine/_updateEnabled/_readyNotified` 与装载、就绪横幅订阅、后台检查迁入；Core 状态机与 Infrastructure 客户端不动。`new HttpClient` 迁至 Infrastructure `UpdateHttpClient.Create()`——主工程 D005 的 `new HttpClient` 豁免行消失。
3. **首启引导端口**：Core 定义 `IFirstBootBootstrap`（`IsNeeded`/双闸门/`Resolve`/`RegisterCliShim`/`Start(onRuntimeReady)`/`Cancel`/`WaitSettledAsync(timeout, ct)`）与 `IFirstBootUi`（引导进度/插件引导帧/日志的语义方法）；实现 `Infrastructure/Services/FirstBootBootstrapService`（dsh 进程交互面：npm 引导重试环、随包/可选插件装配、CLI shim、宿主 `StartAsync`），组合根经 `Services/FirstBootUi` 适配页面向 `PagePump` 注入。端口值类型强类型化：`BootstrapStep` 迁 Core（页面步骤序单一事实源），插件结论用 `PreinstallChoice`——Core↔Presentation 端口不传裸 string 帧标识。`_bootstrapSettled` TCS 私有化进服务——监督器与共享 home 横幅经类型化 `WaitSettledAsync` 参数等待；壳侧导航收尾（两跳/IPC 授权）留组合根，经 `Start` 就绪回调 `EnterMainUiAsync` 接回。`RuntimeBootstrapGate`/`BootstrapSettleGate` 迁 Core（纯逻辑/端口面）。
4. **单实例退出管道** `Core/ExitPipeline`：有序步骤（cancel→stop→release→disposeListener→close→watchdog）是构造数据，内嵌 once-guard 保证幂等；托盘有序退出、Run 尾部回收、自更新兜底强退共用同一实例，看门狗/兜底随管道。`ExitOrchestration` 静态编排退役。
5. **DI 注册构造注入**：`RegisterServices/RegisterTrayServices` 的命令路由改收服务实例（托盘控制器/两闸门/关窗闸门与偏好/更新协调器），不再捕获组合根裸字段。
6. **记序网适配**：`ExitOrchestrationTests` → `ExitPipelineTests`（保留全序 3 用例 + 新增 once-guard 幂等 3 用例）；`CompositionRootSequenceTests` 第二网改为钉「落定句柄创建先于引导任务启动」（TCS 已私有化）。

## Consequences

- 组合根实例字段 32 → 18：C 类跨线程共享态全数迁出；剩余为 B 类顺序资源（host/marker/supervisor/app/windowAccessor/webUrl/cancel 源）、A 类 dev 配置 2 个（批次 3 IOptions）与服务引用 4 个。
- 评审收口：FULL 三审（R1/R2/R3）0 Blocker；R1 5 条 + R2 3 条 + R3 5 条 Suggestion 收口（死存储/零消费者属性/不可达空分支/多余惰性委托/镜像字段/真空 catch 命名/兜底回收日志/端口裸 string 类型化；另修 `CliShimRegistrar` 悬空 cref、batch0 卷位置与测试名改写、Services 伞目录 ADR 目标树计数与新类型落点）。
- `dotnet build` 0 警告；`dotnet test` 624/624（基线 621 + 新增 3 once-guard；批次 0 网全过）；机械化门禁全绿。
- 零行为变更自证：无 env 注入变化 / 既有公共类型构造签名不变（新增类型为新增面；`RuntimeBootstrapGate`/`BootstrapSettleGate` 仅迁命名空间，签名不变）/ spawn 形态不变 / 可观察副作用与 async await 序逐一保持（引导重试环取消仍抛 OCE 由外层取消分支收口同一日志；托盘 `TrayService` 解析仍在 try 内）。
- 门禁面：主工程 D005 `new HttpClient` ignore 行消失；组合根无新增 ignore。

## Alternatives considered

- **引导服务留 Presentation、只搬方法**：dsh 进程/网络/文件交互是外部边界，R3 要求端口在 Core、实现进 Infrastructure；留 Presentation 违反分层。落败。
- **托盘/更新协调器也进 Infrastructure**：两者直触 Ryn 窗口/托盘与页面注入（Presentation 专属），Infrastructure 引 Ryn 违反 R2 程序集断言。落败。
- **退出管道保留静态 `ExitOrchestration` 仅加实例壳**：有序参数序列仍在调用点展开，幂等仍靠各步自带守卫而非单点构造。落败——管道实例 + once-guard 才把幂等搬进机器。
- **本批一并 IOptions 化 A 类 dev 配置**：属批次 3 拍板范围，越界会打乱批次边界=恢复点纪律。落败。
- **引导 UI 端口直接暴露 `PreinstallFrame`**：帧形状属 Presentation 页面契约，暴露给 Core/Infrastructure 会把 UI 线协议带进内层。落败——端口收语义方法，帧形状留实现侧。
