using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 装配期（Build 之前）接线产物的构造函数（ADR sequencer-resolution-injection 的 C3 压舱）：引导装配、
/// 关窗闸门/自更新栈/托盘控制器、运行时起步用例都是「容器之前的运行期产物」——它们必须在 Build 前就位
/// （引导服务要宿主/窗口提供者），装不进解图；但把构造写在装配面里会让 C3 的「根 new 自有类型」读数长期顶格。
/// 此处按注册层的构造纪律收拢（<c>verify-compose-root</c> 的 C5 文案即「resolve via DI or register the
/// construction in *Registration.cs」），装配面只调用并登记实例。装配期共享态同样在此一次造好
/// （见 <see cref="CreateSharedState"/>：壳转发器在 Build 前即被消费，故必须早于容器存在，
/// ADR 组合根寿命与配置单一来源 D1）。
/// 语句与搬迁前逐句等价：调用位、次序与既有 I/O 语义都不变（`bootstrap.Resolve()` 读配置/PATH 并同步跑
/// dsh 版本探测、`CloseBehaviorPreference` 读偏好文件、`updates.Load()` 装载状态），搬的只是文件归属。
/// </summary>
internal static class PreBuildRegistration
{
    /// <summary>进程级资源的唯一释放者（ADR 组合根寿命与配置单一来源 D2）：只造壳，资源由头部逐项登记，
    /// 根在 finally 释放一次。</summary>
    /// <returns>资源拥有者。</returns>
    public static ShellResources CreateShellResources()
    {
        ShellResources resources = new();
        return resources;
    }

    /// <summary>装配期共享态一次创建（ADR 组合根寿命与配置单一来源 D1）：超时家/标题栏参数两个配置模型与
    /// 语言单点、壳转发器在这里造好，前 Build 消费与编排期容器解析拿到同一实例。超时家是配置的唯一
    /// <c>Load</c> 点（D4）：其余消费方按值接收，不各自解析。窗口脚本求值端口同样在此唯一创建
    /// （访问器以提供者惰性供给，Build 后才有值；ADR architecture/2026-10-21-window-script-target-port）。</summary>
    /// <param name="accessorProvider">窗口访问器提供者（惰性；Build 前为 null）。</param>
    /// <returns>共享态（值）；容器登记由 <c>AddBootstrapSharedState</c> 承担。</returns>
    public static SharedState CreateSharedState(Func<CurrentWindowAccessor?> accessorProvider)
    {
        var timeouts = RuntimeTimeouts.Load(AppContext.BaseDirectory, HostLog.Write);
        return new SharedState(
            timeouts,
            CaptionBarOptions.Load(AppContext.BaseDirectory, HostLog.Write),
            new UiLocale(new DesktopUiLocaleStore(HostLog.Write), new OsLocaleProbe()),
            new DshShellForward(),
            new PagePump(timeouts),
            new WindowScriptTarget(accessorProvider));
    }

    /// <summary>首启引导服务与启动期 A 类配置（原装配面 <c>ResolveRuntimeAndDev</c>）：全局 node/dsh 引导、
    /// 插件装配、CLI shim、宿主启动从装配面下沉；页面反馈经 FirstBootUi 注入，宿主/窗口以提供者延迟供给
    /// （调用点均晚于 Build 赋值），UI 语言同样惰性（引导任务实际启动时求值）。</summary>
    /// <param name="hostProvider">宿主提供者（Build 后可用）。</param>
    /// <param name="isEnglishProvider">UI 语言提供者（惰性求值）。</param>
    /// <param name="shared">装配期共享态（页面注入单点、脚本求值端口与超时家）。</param>
    /// <returns>启动头部产出（引导服务 + 启动配置）。</returns>
    public static Preflight CreatePreflight(
        Func<HarnessRuntimeHost> hostProvider,
        Func<bool> isEnglishProvider,
        SharedState shared)
    {
        IFirstBootBootstrap bootstrap = new FirstBootBootstrapService(
            hostProvider,
            new FirstBootUi(shared.ScriptTarget, shared.Pump),
            isEnglishProvider,
            shared.Timeouts,
            HostLog.Write);
        bootstrap.Resolve();
        return new Preflight(bootstrap, LaunchOptions.Resolve(HostLog.Write));
    }

    /// <summary>关窗闸门/自更新栈/托盘控制器装配（原装配面 <c>InitCloseGateAndUpdateStack</c>）：构造只收惰性
    /// 委托与配置，与运行时产出无交互——装配在 Build 之前（原 spawn→装配 反转为 装配→spawn）。委托均在调用期
    /// 求值（页面交互/后台任务），提供者赋值晚于本方法返回，与原接线槽语义等价。</summary>
    /// <param name="preflight">启动头部产出（dev 判定）。</param>
    /// <param name="supervisorCts">监督器取消令牌源（自更新后台任务的取消面）。</param>
    /// <param name="windowProvider">原生窗口提供者（惰性）。</param>
    /// <param name="accessorProvider">窗口访问器提供者（惰性；未就绪时为 null）。</param>
    /// <param name="exitProvider">退出管道提供者（惰性）。</param>
    /// <param name="shared">装配期共享态（页面注入单点、语言单点与超时家）。</param>
    /// <returns>自更新栈装配产出与托盘控制器。</returns>
    public static (UpdateSetup Updates, TrayController Tray) CreateUpdateAndTrayAssembly(
        Preflight preflight,
        CancellationTokenSource supervisorCts,
        Func<IRynWindow> windowProvider,
        Func<CurrentWindowAccessor?> accessorProvider,
        Func<ExitPipeline> exitProvider,
        SharedState shared)
    {
        // hide-to-tray 关窗闸门（ADR shell-tray-hide-to-tray）：托盘「退出」与自更新安装路径
        // 先批准再 Close。用户普通关窗是否转隐藏由 closeBehavior 偏好裁决（默认 true 保持
        // 历史行为）；托盘未就绪时拦截不生效（关窗直退）。
        var closeGate = new Tray.CloseGate();
        var closeBehavior = new CloseBehaviorPreference(
            Path.Combine(HarnessRuntimeHost.ResolveDshHome(), CloseBehaviorPreference.FileName));

        // 自更新协调器在此构造并装载（早于 BuildApp）：Application 用例住 Core（ADR
        // update-coordinator-core-port），栈协作经四端口注入（宿主事实/feed/下载/安装器各一实现，
        // ADR architecture/2026-10-20-update-stack-adapter-split），
        // UI 交接以委托闭包接线（PagePump/横幅/关窗闸门/退出管道）——装配层只装配，编排策略随用例可单测。
        var updateEnvironment = new UpdateEnvironmentAdapter(HostLog.Write);
        var updateFeed = new ReleaseFeedAdapter(HostLog.Write);
        var updateDownloader = new PackageDownloaderAdapter(HostLog.Write);
        var updateInstaller = new UpdateInstaller(HostLog.Write);
        var updates = new UpdateCoordinator(
            preflight.Launch.IsDev,
            updateEnvironment,
            updateFeed,
            updateDownloader,
            updateInstaller,
            () => supervisorCts.Token,
            state => PagePump.PushUpdateState(shared.ScriptTarget, state),
            (version, token) => shared.Pump.ShowBannerWhenReadyAsync(
                shared.ScriptTarget, UpdateBanner.ReadyScript(version, shared.UiLocale), token),
            closeGate.ApproveExit,
            () => accessorProvider()?.Current?.Close(),
            ct => exitProvider().ScheduleExitFallback(ct),
            HostLog.Write);
        updates.Load();

        // 托盘控制器在此构造（早于 BuildApp/ShowTray），窗口与 Ryn 服务以惰性委托注入——
        // 控制器持有 hide-to-tray 拦截、唤回采样、菜单重建与关窗闸门/偏好（供路由构造注入）。
        var tray = new TrayController(
            windowProvider,
            accessorProvider,
            closeGate,
            closeBehavior,
            shared.UiLocale,
            updates.Machine,
            HostLog.Write);

        return (new UpdateSetup(updates), tray);
    }

    /// <summary>单实例仲裁（ADR single-instance-launcher-activation）：false = 已有主实例（已通知），
    /// 调用方退出；<paramref name="onShowRequested"/> 是主实例收到 <c>show</c> 时的唤回回调。
    /// 锁地址解析与绑定策略住 Infrastructure。</summary>
    /// <param name="preflight">启动头部产出（dev 判定）。</param>
    /// <param name="timeouts">运行时超时家（IPC 预算）。</param>
    /// <param name="onShowRequested">二启唤回回调（主窗就绪前由唤回单点挂起）。</param>
    /// <param name="listener">仲裁成功时持有的监听器；平台不支持/降级时为 null。</param>
    /// <returns>true = 本进程是主实例。</returns>
    public static bool TryBecomePrimary(Preflight preflight, RuntimeTimeouts timeouts, Func<Task> onShowRequested, out PrimaryListener? listener) =>
        LauncherActivation.TryBecomePrimary(
            preflight.Launch.IsDev,
            onShowRequested,
            TimeSpan.FromSeconds(timeouts.IpcAcceptRetryDelaySeconds),
            TimeSpan.FromSeconds(timeouts.IpcServeTimeoutSeconds),
            TimeSpan.FromSeconds(timeouts.NotifyPrimaryTimeoutSeconds),
            out listener);

    /// <summary>启动前置用例（profile 生命周期 + 随包插件安装；均 headless，spawn 前执行）。</summary>
    /// <param name="timeouts">运行时超时家（插件安装探针预算）。</param>
    /// <returns>profile 前置与随包插件安装用例。</returns>
    public static (ProfileLifecycle Profile, CompanionPreSpawn Companion) CreateStartupPrerequisites(RuntimeTimeouts timeouts) =>
        (new ProfileLifecycle(), new CompanionPreSpawn(timeouts));

    /// <summary>运行时起步用例（原装配面 <c>RegisterServices</c> 内联构造）：spawn→铸币→收 URL 的依赖全在手
    /// （引导服务来自启动头部，超时/铸币来自共享单例）。</summary>
    /// <param name="preflight">启动头部产出（引导服务）。</param>
    /// <param name="timeouts">运行时超时家（spawn 预算）。</param>
    /// <param name="shellForward">壳转发器（铸币回调）。</param>
    /// <returns>运行时起步用例。</returns>
    public static IRuntimeStarter CreateRuntimeStarter(
        Preflight preflight, RuntimeTimeouts timeouts, DshShellForward shellForward) =>
        new RuntimeStarter(
            preflight.Bootstrap,
            TimeSpan.FromSeconds(timeouts.SpawnTimeoutSeconds),
            shellForward.MintAsync,
            HostLog.Write);
}
