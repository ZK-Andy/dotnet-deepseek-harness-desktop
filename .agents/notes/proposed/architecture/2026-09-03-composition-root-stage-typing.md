# Agent Note: composition-root-stage-typing（组合根 Run() 阶段方法重构——时序由类型流表达）

Status: proposed

## Problem

`DesktopBootstrap`（组合根，4 个 partial 共约 1250 行，ADR `split-program-main-god-function`）的入口 `Run()` 现以**顺序链 + 共享字段**编排约 13 个启动阶段：

```csharp
public int Run()
{
    ResolveRuntimeAndDev();
    if (!AcquireSingleInstance()) return 0;
    EnsureDesktopProfile();
    try
    {
        SetupHostAndMarker();
        InstallCompanionBeforeSpawn();
        StartRuntime();
        InitCloseGateAndUpdateStack();
        BuildApp();
        RunBootstrapIfNeeded();
        ShowTray();
        SetupSupervisor();
        SetupHealthMonitor();
        StartUpdateCheck();
        SharedHomeBannerTask();
        return RunAppLoop();
    }
    finally { _host?.Dispose(); _supervisorCts?.Dispose(); }
}
```

阶段间存在严格依赖序（例：`StartRuntime` 必须在 `BuildApp` 前、`_supervisorCts` 必须在 `RegisterServices` 捕获前赋值、`_bootstrapGate`/`_preinstallGate` 必须在命令路由注册前创建）。但该顺序**只由注释与字段赋值时点维持，编译器不检查**——改错依赖序不会编译失败，只会产生运行期怪病（如窗口先于运行时构建、门控晚于路由注册）。状态以 40+ 字段散布四个 partial，跨文件共享靠"赋值时点与原 Main 语句一致"的注释契约维系。

组合根"显式有序"的形态本身是优点（可读、可审计、贴合 ADR）；真正的债是**顺序无法执行化**——注释承诺的依赖序没有机器校验，改动的安全网只靠人的纪律与三重审核。

2026-09-03 代码质量评估（外部视角）建议以"`BootstrapContext` record 分组 23 字段"降低组合根复杂度——该方案在讨论中**被否决**（见 Alternatives）：字段多不是真问题，把 40+ 字段包进 context 不消除任何真实耦合，只加一层寻址、降低时序可读性。本 ADR 提出另一方向：以**类型流（阶段返回值）表达顺序**，而非注释。

## Proposal

把 `Run()` 的顺序链重构为**有返回值的阶段方法链**：前一阶段的返回结果（携带该阶段产出的依赖）作为下一阶段的输入，依赖关系由**类型系统表达**——缺前置产出的阶段在编译期即无法调用。

形态示意（非最终签名，待依赖图后定稿）：

```csharp
public int Run()
{
    PreflightResult preflight = Preflight();          // 运行时/环境/单实例/隔离
    if (!preflight.IsPrimary) return 0;

    using HostScope host = StartHost(preflight);       // marker/profile/宿主/随包插件
    RuntimeStartResult runtime = host.StartRuntime();  // 起 dsh，产出 webUrl
    AppScope app = host.BuildApp(runtime);             // Ryn 应用（消费 webUrl）
    app.RunLifecycle();                                // 监督/健康/更新/主循环
    return 0;
}
```

**执行纪律（用户拍板）**：
1. **先画依赖图**：穷举 13 步 + 40+ 字段的读写关系，产出阶段划分与依赖边（哪些字段仅阶段内、哪些跨阶段传递、哪些是"全局单例"如 HostLog）。
2. **零行为变更拆分**：纯结构重构，语句顺序/分支/异常边界/日志语料逐一对应；不得夹带任何行为修正。
3. **三重审核**（R1/R2/R3 串行）：确认"零行为变更"成立，ADR 与代码同步收口。

### 依赖图定稿（2026-09-03 画图后拍板）

**36 字段消费模式三分（这是划分依据，非新方案）：**
- **A 启动配置**（`ResolveRuntimeAndDev` 一次解析、5+ 远亲消费）：`_bootstrapOptions/_bootstrapNeeded/_bootstrapGate/_preinstallGate/_isDev/_devAutoIsolated` → **保持字段**（全壳事实性配置，强行参数流=BootstrapContext 覆辙）。
- **B 顺序资源**（主链"先建后用"、消费集中在相邻后续阶段）：`_closeGate/_closeBehavior/_updateMachine/_updateEnabled/_readyNotified/_iconPath/_trayAvailable` → 候选进阶段句柄。
- **C 跨线程共享态**（被后台 Task 闭包/激活回调/更新 install 委托捕获、或跨阶段被多异步改写）：`_host/_supervisorCts/_webUrl/_windowAccessor/_marker/_previousRunUnclean/_maximizedAtHide/_instanceListener/_supervisor/_supervisorTask/_orderlyQuit/_updateExitReaper/_healthMonitor/_uiLocale/_bootstrapSettled/_bootstrapCts/_trayReady/_app/_supervisorCtsRef` → **保持字段**（ADR Risks"不迁移字段归属"即指此；后台闭包零改动，字段迁移范围受控）。

**顺序依赖边（改错即启动怪病，由类型流锁定）：**
`Resolve→Acquire(读isDev)`、`SetupHost→InstallCompanion→StartRuntime`（companion 必须 spawn 前装）、
`StartRuntime→BuildApp`（BuildApp 消费 webUrl）、`InitCloseGate→BuildApp`（注册消费 closeGate/updateMachine）、
`BuildApp→ShowTray/SetupSupervisor`（需 app/windowAccessor）、`SetupSupervisor→Health/UpdateCheck/Banner/RunAppLoop`（需 supervisorCts）。

### 最终签名定稿（阶段 token 链，2026-09-03 拍板）

```csharp
public int Run()
{
    PreflightToken preflight = ResolveRuntimeAndDev();        // A 类解析，产 PreflightToken
    if (!AcquireSingleInstance(preflight)) return 0;          // 读 isDev，仅早退
    EnsureDesktopProfile();
    try
    {
        HostToken host = SetupHostAndMarker(preflight);       // SetupHostAndMarker
        InstallCompanionBeforeSpawn(host);                    // 顺序语句
        RuntimeToken runtime = StartRuntime(host);            // StartRuntime → webUrl
        UpdateToken update = InitCloseGateAndUpdateStack(runtime); // InitCloseGate
        AppToken app = BuildApp(runtime, update);             // BuildApp
        RunBootstrapIfNeeded(app);                            // 原 RunBootstrapIfNeeded
        ShowTray(app);                                        // 原 ShowTray
        SupervisorToken sup = SetupSupervisor(app, host);     // SetupSupervisor
        StartBackgroundServices(sup);                         // 三后台（Health+Update+Banner）
        return RunAppLoop(sup, host);                         // 原 RunAppLoop
    }
    finally { /* 等价释放 */ }
}
```

**token 链机制**：阶段方法签名收上一阶段 token、返回本阶段 token；方法**体保持字段读写零动**（后台闭包/辅助方法仍读字段），token 只承载"本阶段已执行"的类型承诺。保护是**偏序非全序**（R1/R2 评审定稿）：token 把**产生者的执行序**钉进类型——跨过产生者直接消费其产出在编译期即缺值；但不锁消费段——整段漏调某消费方法、或两个同 token 消费段乱序，编译仍通过（与重构前同风险，非本机制承诺面）。6 个 token 中唯一例外：`UpdateToken` 被 `BuildApp(runtime, update)` 消费，省略 `InitCloseGateAndUpdateStack` 会让 BuildApp 缺参编译失败——这是唯一"缺失即编译失败"的硬约束，勿简化折叠（折叠会让省略 InitCloseGate 重新可编译）。

**新类型 = `DesktopBootstrap` 嵌套私有 `readonly record struct` token**（A5 判据微调放行嵌套于组合根的类型，理由：编排产物语义上是组合根一部分，进 Services/ 污染子域）。

**配套清理（已完成，前置批 `62d3ca2`，见 [composition-root-field-cleanup](../../implemented/process/2026-09-03-composition-root-field-cleanup.md)）**：原列三处字段收口已由字段清理批落地：
- `_updateWindow` 折叠 → `_windowAccessor`、`_startupNavigationSettled` 删除（留注释说明为何不再需要 TCS 门控）、`_supervisorCtsRef` 保留 + 补注释。本 ADR 执行时此三处已清，无待办。

## Alternatives considered

- **`BootstrapContext` record 分组字段（外部评估建议）**：把 23/40+ 字段按生命周期分组为小型上下文对象传给各阶段。**落败**——字段多不是组合根的真问题；context 不消除耦合（各阶段仍共享同一组合根的字段）、只加一层寻址，且把"顺序靠注释"变成"顺序靠参数 + context 内字段"，可读性不升反降；与组合根"显式有序铺开"的价值相悖。**不引入**。
- **维持现状（顺序靠注释 + 字段赋值时点）**：零风险，但依赖序无机器校验，改错只运行时怪病；已有 2 个 TODO 表明"拆分到一半接线没收口"，现状的可持续性已到临界。**落败**（用户拍板：真重构，方向合适）。
- **引入阶段门/运行期断言**（讨论中提出的折中）：每阶段开头断言前置字段已赋值，把注释承诺变运行期 fail loud。成本低，但只把"NRE 前移"成"断言失败"，未让顺序进入类型系统；且组合根是启动期单次执行，运行期断言的边际价值有限。**列为低优先备选**——若完整类型流重构风险过大，可作为过渡。
- **彻底重写组合根为端口/适配器 + 依赖注入容器全托管**：R3 边界抽象的极端形态。**落败**——薄壳组合根过度设计；Ryn 生命周期/退出顺序/启动门控是本项目最依赖显式时序的部分，容器化会掩盖顺序、反而更难审计。

## Acceptance criteria

- `Run()` 阶段链重构落地：依赖序由阶段返回类型表达，缺前置产出的调用在编译期失败。
- 阶段 token 为 `DesktopBootstrap` 嵌套私有类型；A5 门禁判据同步放行（ArchitectureTests 改判据）。
- 重构为**零行为变更**：语句顺序/分支/异常边界/日志语料逐一对应；`dotnet test` 全绿（当前 449/449）、0 警告、`dotnet format` 绿。
- 三重审核（R1/R2/R3）确认零行为变更成立，无 Blocker。
- 配套三字段清理已完成（前置批 `62d3ca2`）：`_updateWindow` 折叠、`_startupNavigationSettled` 删除、`_supervisorCtsRef` 补注释；2 个 TODO 摘除。
- 无新 TODO 引入；`DesktopBootstrap` 总行数不因重构膨胀。

## Risks

- **时序回归风险（高）**：这是全壳最核心的启动时序，任何阶段划分错误都会导致启动期怪病（窗口先于运行时、门控晚于路由、退出顺序错乱）。缓解：严格"先依赖图 → 零行为变更 → 三重审核"，且阶段方法仅在**原调用点**重构、不重排业务执行序。
- **阶段记录类型膨胀**：若每个阶段都建 record，类型数上升。缓解：仅对"确有跨阶段依赖产出"的阶段建类型，纯内部阶段可返回既有类型或 `void`。
- **与自更新/监督器/引导的既有接线耦合**：这些子域大量捕获组合根字段（委托注入），阶段化可能暴露"哪些字段其实该是子域内部状态"。缓解：本 ADR 只做顺序表达重构，不迁移字段归属（那是独立子域治理，另立项）。
- **"零行为变更"被证伪**：若依赖图显示某阶段实际存在隐藏重排需求，宁可在本重构前单独修正，也不夹带。

## Related

- [split-program-main-god-function](../../implemented/architecture/2026-08-30-split-program-main-god-function.md)（implemented）：`Program.Main` → `DesktopBootstrap` 的原始拆分 ADR；本重构在其基础上把"顺序靠注释"推进为"顺序靠类型"。
- [ryn-navigation-callbacks](../../implemented/feature/2026-08-28-ryn-navigation-callbacks.md)（implemented）：`_startupNavigationSettled` 横幅门控的出处；本 ADR 判定其机制已死、接线残留，配套清理时 cut 并留注释。
- [shell-firstboot-hardening](../../implemented/bug-fix/2026-08-24-shell-firstboot-hardening.md)（implemented）：横幅门控要防的实机 bug（v0.3.0 切换日横幅消失）；cut 前须确认 `ShowBannerWhenReadyAsync` 重试环已覆盖其场景。
- 待办区（`HANDOFF-todos.md`，本地 gitignore 行动文档）「组合根阶段方法重构」「组合根 TODO 收口」两条：本 ADR 是这两条待办的细节单一事实源。
