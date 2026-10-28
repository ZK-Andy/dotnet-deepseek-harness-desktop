namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 装配期（窗口与桥之前的运行期产物）构造函数：引导装配、共享态、单实例仲裁与运行时起步用例都是
/// 「容器之前的运行期产物」——引导服务要宿主提供者、共享态被启动头部消费，装不进容器；把构造写在
/// 装配面里会让 C3 的「根 new 自有类型」读数长期顶格。此处按注册层的构造纪律收拢
/// （<c>verify-compose-root</c> 的 C5 文案即「resolve via DI or register the construction in
/// *Registration.cs」），装配面只调用并接线。
/// 语句与搬迁前逐句等价：调用位、次序与既有 I/O 语义都不变（<c>bootstrap.Resolve()</c> 读配置/PATH
/// 并同步跑 dsh 版本探测、偏好与超时配置各读一次），搬的只是文件归属。
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
    /// 语言单点、壳转发器在这里造好，启动头部与窗口/桥装配拿到同一实例。超时家是配置的唯一
    /// <c>Load</c> 点（D4）：其余消费方按值接收，不各自解析。</summary>
    /// <returns>共享态（值）；长命单例由调用方显式传递。</returns>
    public static SharedState CreateSharedState()
    {
        var timeouts = RuntimeTimeouts.Load(AppContext.BaseDirectory, HostLog.Write);
        return new SharedState(
            timeouts,
            CaptionBarOptions.Load(AppContext.BaseDirectory, HostLog.Write),
            new UiLocale(new DesktopUiLocaleStore(HostLog.Write), new OsLocaleProbe()),
            new DshShellForward(),
            new PagePump(timeouts));
    }

    /// <summary>首启引导服务与启动期 A 类配置：全局 node/dsh 引导、插件装配、CLI shim、宿主启动从装配面下沉；
    /// 页面反馈经 FirstBootUi 注入，宿主/窗口以提供者延迟供给（调用点均晚于赋值），UI 语言同样惰性
    /// （引导任务实际启动时求值）。</summary>
    /// <param name="hostProvider">宿主提供者（窗口/桥就绪后可用）。</param>
    /// <param name="languageProvider">UI 语言提供者（惰性求值）。</param>
    /// <param name="shared">装配期共享态（页面注入单点与超时家）。</param>
    /// <param name="scriptTargetProvider">页面脚本求值端口提供者（调用期取值；页面桥的惰性适配器）。</param>
    /// <returns>启动头部产出（引导服务 + 启动配置）。</returns>
    public static Preflight CreatePreflight(
        Func<HarnessRuntimeHost> hostProvider,
        Func<UiLanguage> languageProvider,
        SharedState shared,
        Func<IWindowScriptTarget> scriptTargetProvider)
    {
        IFirstBootBootstrap bootstrap = new FirstBootBootstrapService(
            hostProvider,
            new FirstBootUi(scriptTargetProvider, shared.Pump),
            languageProvider,
            shared.Timeouts,
            HostLog.Write);
        bootstrap.Resolve();
        return new Preflight(bootstrap, LaunchOptions.Resolve(HostLog.Write));
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

    /// <summary>运行时起步用例：spawn→铸币→收 URL 的依赖全在手（引导服务来自启动头部，超时/铸币来自共享单例）。</summary>
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
