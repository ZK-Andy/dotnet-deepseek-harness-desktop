# Agent Note: composition-root-stage-typing（组合根 Run() 阶段方法重构——时序由类型流表达）

Status: implemented

Review: FULL/2026-09-03/R1=ok R2=ok R3=ok

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

2026-09-03 代码质量评估（外部视角）建议以"`BootstrapContext` record 分组 23 字段"降低组合根复杂度——该方案在讨论中**被否决**（见 Alternatives）：字段多不是真问题，把 40+ 字段包进 context 不消除任何真实耦合，只加一层寻址、降低时序可读性。本 ADR 落地另一方向：以**类型流（阶段返回值）表达顺序**，而非注释。

## Decision

`Run()` 的顺序链已重构为**有返回值的阶段方法链**：前一阶段的返回结果（携带该阶段产出的依赖）作为下一阶段的输入，依赖关系由**类型系统表达**——缺前置产出的阶段在编译期即无法调用。

落地方案经依赖图定稿（见下）后收敛为**阶段 token 链**（非最初的 Data-carrying 句柄形态）：6 个空载荷 `readonly record struct` marker，不携带状态、只承载"本阶段已执行"的类型承诺。

**执行纪律（用户拍板，重构时遵守）**：
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

## Consequences

**已达成（2026-09-03 `7fe0123` 落地）**：`Run()` 阶段链重构完成，依赖序由阶段返回类型表达（偏序非全序，见 Decision）；6 阶段 token 为 `DesktopBootstrap` 嵌套私有 `readonly record struct`，A5 门禁判据同步放行；重构零行为变更（语句序/分支/异常边界/日志语料逐一对应，`dotnet test` 449/449、0 警告、format 绿）；配套三字段清理由前置批 `62d3ca2` 完成；无新 TODO 引入。三重审核 R1/R2/R3 收口确认零行为变更成立（R1/R2 措辞修正、R3 无 Blocker）。

**残余边界与代价**：
- **时序回归风险（本重构的固有面）**：全壳最核心启动时序，阶段划分错误会致启动期怪病。缓解已落地：产生者执行序入类型（编译期锁）、消费段时序仍靠原调用点纪律 + 注释（偏序边界，勿误读为全序）。
- **token 链是偏序非全序**：消费段整段漏调/乱序仍编译通过——这是设计边界，非缺陷（详见 Decision）。
- **阶段记录类型未膨胀**：仅 6 个 token（空载荷 marker），未逐阶段建 Data-carrying record。
- **字段归属未迁移**：后台闭包/事件委托捕获的字段保持字段态，子域治理另立项。

## Related

- [split-program-main-god-function](../../implemented/architecture/2026-08-30-split-program-main-god-function.md)（implemented）：`Program.Main` → `DesktopBootstrap` 的原始拆分 ADR；本重构在其基础上把"顺序靠注释"推进为"顺序靠类型"。
- [ryn-navigation-callbacks](../../implemented/feature/2026-08-28-ryn-navigation-callbacks.md)（implemented）：`_startupNavigationSettled` 横幅门控的出处；其配套清理已由前置批 `62d3ca2` 完成（cut + 留注释）。
- [shell-firstboot-hardening](../../implemented/bug-fix/2026-08-24-shell-firstboot-hardening.md)（implemented）：横幅门控要防的实机 bug（v0.3.0 切换日横幅消失）；cut 前已确认 `ShowBannerWhenReadyAsync` 重试环覆盖其场景（62d3ca2）。
- [review-tier-escape-proofing](../../implemented/process/2026-09-03-review-tier-escape-proofing.md)（implemented）：本重构触发评审档逃逸 → 机械强制机制；本 ADR 的三重审核经该机制闭环。
- [composition-root-field-cleanup](../../implemented/process/2026-09-03-composition-root-field-cleanup.md)（implemented）：配套三字段清理的前置批。
