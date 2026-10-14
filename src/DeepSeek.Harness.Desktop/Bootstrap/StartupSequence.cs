namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动编排服务（ADR compose-root-form-separation）：阶段主链与用例编排自组合根整体搬迁至此
/// （语句序/分支/异常边界逐一对应，纯搬家零行为变更）；组合根只装配与触发（R1）。Ryn 无 hosted-service
/// 机制，编排器由组合根在 Build 后解析并触发（结构必然，见 ADR Alternatives）。
/// 形状＝排序器：两相入口 <see cref="RunStartupAsync"/>（异步相，I/O 起步链）与 <see cref="RunMainLoop"/>
/// （同步相，Ryn 面尾段 + 主循环 + 退出编排）都只按序触发用例/阶段（无分支、无域文本、不构造服务、
/// 无同步桥——机器闸见 <c>verify-compose-root.py</c> C6；ADR entry-bridge-two-phase），用例与依赖经容器
/// 解图供给（ADR 组合根机制收官终局的 B3）；
/// 阶段产出为正式类型（<see cref="Preflight"/> 等），宿主/托盘等运行期单例在调用点解析、取消令牌源经构造注入；
/// 监督任务以局部 <see cref="Task"/> 流转，排序器不构造任何自有类型（C6 白名单空集，ADR sequencer-stage-value-retirement）。
/// 本文件承载启动主链与产生段；监督接线在 <c>StartupSequence.Supervision.cs</c>。
/// 导航/监督/顶栏注入/启动期告知/退出编排已随用例下沉（ADR 组合根机制收官 step-1 的 B1/B2 与终局的 B3：
/// <c>Core.Ui.MainUiEntry</c> + <c>Core.Bootstrap.RuntimeSupervision</c> + <c>PageBridge.CaptionBarInstaller</c>
/// + <c>Infrastructure.Bootstrap.StartupNoticeService</c> + <c>Core.Bootstrap.AppExitOrchestration</c>）。
/// </summary>
internal sealed partial class StartupSequence : IStartupSequence
{
    private readonly AppSetup _app;
    private readonly Preflight _preflight;
    private readonly UpdateSetup _updates;
    private readonly PrimaryListener? _instanceListener;
    // 运行时起步用例（ADR 组合根机制收官先行批）：spawn→铸币→收 URL 下沉 Core 可单测；
    // 由容器解图注入（本类无缺省组装分支）。
    private readonly IRuntimeStarter _runtimeStarter;
    private readonly RuntimeTimeouts _timeouts;
    // 退出编排用例（ADR app-exit-orchestration-core-usecase）：主循环承载与收口顺序住 Core、可单测，
    // 排序器只触发；容器解图注入（收口动作仍在本类收口点惰性求值——退出管道经宿主派生）。
    private readonly Core.Bootstrap.AppExitOrchestration _exit;
    // 监督器取消令牌源（装配期创建、容器单例；寿命 = 本次 Run）：退出路径取消 + 监督/健康/告知任务共用。
    private readonly CancellationTokenSource _supervisorCts;
    // 运行期解析面（ADR sequencer-resolution-injection）：受时点约束的运行期单例收成一份构造数据，
    // Value 落在原解析点；排序器不持 IServiceProvider。
    private readonly RuntimeResolutions _resolutions;

    /// <summary>创建编排服务（构造参数经容器解图，ADR 组合根机制收官终局的 B3）：共享态与用例依赖由
    /// 容器供给，接线层不再逐个手传；唯一例外是 <see cref="AppSetup"/>——Ryn 不把 <c>RynApplication</c>
    /// 登记进容器（<c>RynApplicationBuilder.Build()</c> 只返回实例），Build 后才存在的应用产出装不进解图，
    /// 由接线层解析时显式供给。运行期解析面经构造注入（<see cref="RuntimeResolutions"/>，Value 落在原解析点），
    /// 取消令牌源亦经构造注入。</summary>
    /// <param name="app">应用装配产出（Build 后唯一装不进容器的值，显式供给）。</param>
    /// <param name="preflight">容器之前的头部产出（引导服务与启动配置）。</param>
    /// <param name="updates">自更新栈装配产出（协调器已装载）。</param>
    /// <param name="instanceListener">单实例监听器持有（编排尾部释放锁地址）。</param>
    /// <param name="runtimeStarter">运行时起步用例（spawn→铸币→收 URL）。</param>
    /// <param name="timeouts">运行时超时家（配置模型单例；编排期留痕与 join 预算消费）。</param>
    /// <param name="exit">退出编排用例（主循环承载 + 收口顺序）。</param>
    /// <param name="supervisorCts">监督器取消令牌源（容器单例；退出取消与监督/健康/告知任务共用）。</param>
    /// <param name="resolutions">运行期解析面（Lazy 携带；Value 落在原解析点）。</param>
    public StartupSequence(
        AppSetup app,
        Preflight preflight,
        UpdateSetup updates,
        InstanceListenerSetup instanceListener,
        IRuntimeStarter runtimeStarter,
        RuntimeTimeouts timeouts,
        Core.Bootstrap.AppExitOrchestration exit,
        CancellationTokenSource supervisorCts,
        RuntimeResolutions resolutions)
    {
        _app = app;
        _preflight = preflight;
        _updates = updates;
        _instanceListener = instanceListener.Listener;
        _runtimeStarter = runtimeStarter;
        _timeouts = timeouts;
        _exit = exit;
        _supervisorCts = supervisorCts;
        _resolutions = resolutions;
    }

    /// <summary>启动异步相（排序器主链的 I/O 段）：profile 前置 → 宿主产出 → 随包插件安装 → 运行时起步。
    /// 本相唯一的 await 在起步步，续体在池线程（<c>ConfigureAwait(false)</c>）——故 Ryn 面调用（托盘、
    /// 顶栏注入、主循环）一律留同步相（ADR entry-bridge-two-phase）。
    /// 宿主/令牌源寿命由接线层拥有（容器单例），此处不释放。</summary>
    public async Task RunStartupAsync()
    {
        // profile 前置（migrate→recover→ensure→reconcile 用例编排住 Infrastructure.ProfileLifecycle）
        _resolutions.Profile.EnsureReady();
        // 宿主装配产出（容器惰性单例）：首次解析即创建；必须先于随包插件安装与 spawn（偏序 = 宿主已建、spawn 前），
        // 偏序在本相内自洽，不依赖组合根的 eager 解析兜底。
        HostSetup host = _resolutions.Host.Value;
        // 随包插件 spawn 前安装（策略谓词与安装住 Infrastructure.CompanionPreSpawn；偏序 = 宿主已建、spawn 前）
        _resolutions.Companion.EnsureInstalled(_preflight.Bootstrap.IsNeeded, _preflight.Launch);
        // 运行时起步用例（ADR 组合根机制收官先行批）：shim 注册→spawn 等 URL→壳铸币由 IRuntimeStarter
        // 承载、可单测（实现见 Core.Bootstrap.RuntimeStarter）。dsh web URL 就此收官：下游各自从宿主或
        // 回调取得 URL，无跨阶段消费点——不设阶段产出（死载荷会被 CompositionRootSequenceTests 拦下）。
        _ = await _runtimeStarter.StartAsync(host.Host, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>同步相（thread 0）：启动链尾段（引导接回 / 托盘 / 监督 / 健康 / 顶栏 / 更新 / 告知）、
    /// 主循环与退出编排（编排本体住 <see cref="Core.Bootstrap.AppExitOrchestration"/>，
    /// ADR app-exit-orchestration-core-usecase）。<b>必须由接线层在 thread 0 调用</b>——Ryn 主循环与
    /// Ryn 面调用（托盘、顶栏注入）要求（ADR entry-bridge-two-phase）。回收动作在此惰性求值：退出管道
    /// 经宿主派生，首次解析仍落在收口点（ADR sequencer-resolution-injection）。</summary>
    public int RunMainLoop()
    {
        // 宿主解析面命中（异步相已首次解析），同一实例、无 I/O
        HostSetup host = _resolutions.Host.Value;
        RunBootstrapIfNeeded();
        ShowTray();
        Task supervisorTask = SetupSupervisorAsync(host);
        SetupHealthMonitor();
        InstallCaptionBar();
        StartUpdateCheck();
        StartupNoticeTask();
        return _exit.Run(
            () => _app.App.Run(),
            supervisorTask,
            () => _resolutions.ExitPipeline.Value.ReapRuntime(),
            _instanceListener is null ? null : _instanceListener.Dispose);
    }

    private void RunBootstrapIfNeeded()
    {
        // 首启引导（ADR online-first-unbundled-runtime）：窗口先亮（wwwroot 引导页），引导服务后台完成
        // 检测/下载/安装/验证后起 dsh，并把就位 URL 交回调接回壳侧导航；失败推错误态等待用户重试
        // （desktop.bootstrap.retry 经闸门放行）。引导未落定前监督器/插件安装均被门控——依赖序即插入位。
        // 就位后的铸币/等窗/终页裁决是 Core 用例（ADR 组合根机制收官 step-1 的 B1）。
        Core.Ui.MainUiEntry entry = _resolutions.MainUiEntry.Value;
        _preflight.Bootstrap.Start((url, ct) => entry.RunAsync(url, ct));
    }

    /// <summary>托盘就绪化（装配壳）：解析 Ryn 托盘服务并交给控制器（无托盘环境传 null）。</summary>
    private void ShowTray()
    {
        // 托盘在组合根装配期构造（容器单例），ShowTray 必然晚于它
        Tray.TrayController tray = _resolutions.Tray.Value;
        tray.Show(tray.IsAvailable
            ? () => _resolutions.TrayService.Value
            : null);
    }

    private void StartUpdateCheck()
    {
        // 自更新启动对账 + 后台检查一次（失败静默转 error 态，不影响首屏）
        _updates.Updates.Start();
    }
}
