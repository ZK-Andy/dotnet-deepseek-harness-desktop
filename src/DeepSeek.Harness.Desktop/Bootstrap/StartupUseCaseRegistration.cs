using DeepSeek.Harness.Desktop.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动用例注册（ADR 组合根机制收官 step-1 + 终局的 B3）：导航/监督/顶栏注入/启动期告知用例与端口
/// 适配器在注册处组装，接线层只解析触发（R1）；时限参数由配置模型 <c>RuntimeTimeouts</c> 单点快照到
/// Core 侧记录，展示面闭包（横幅构建与推送）同处装配。
/// </summary>
internal static class StartupUseCaseRegistration
{
    /// <summary>登记启动用例与端口适配器（单例；惰性工厂首次解析即创建，与容器既有单例时序一致）。
    /// 顶栏注入与启动期告知的用例本体自排序器下沉至此登记（B3），排序器只留按序触发。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="bootstrap">首启引导服务（监督用例的落定握手句柄来源；告知用例的落定门控）。</param>
    /// <param name="proxy">回环代理源；null = 绑定失败（导航用例降级不导航）。</param>
    /// <returns>服务集合（链式）。</returns>
    public static IServiceCollection AddStartupUseCases(
        this IServiceCollection services, IFirstBootBootstrap bootstrap, DshLoopbackProxy? proxy)
    {
        services.AddSingleton<IMintEpoch>(
            sp => new ShellMintEpochAdapter(sp.GetRequiredService<DshShellForward>(), HostLog.Write));
        services.AddSingleton<IWebViewNavigator>(sp => new WebViewNavigator(
            sp.GetRequiredService<CurrentWindowAccessor>(),
            sp.GetRequiredService<RynNavigationCallbacks>()));
        services.AddSingleton<IRecoverySurface>(sp => new RecoverySurface(
            sp.GetRequiredService<HostSetup>().Host,
            sp.GetRequiredService<CurrentWindowAccessor>(),
            sp.GetRequiredService<IFailureDialog>(),
            sp.GetRequiredService<IDiagnosticsExport>(),
            sp.GetRequiredService<UiLocale>(),
            () => sp.GetRequiredService<IRynWindow>(),
            sp.GetRequiredService<TrayController>(),
            HostLog.Write));
        services.AddSingleton(sp => new MainUiEntry(
            sp.GetRequiredService<IWebViewNavigator>(),
            sp.GetRequiredService<DshShellForward>().MintAsync,
            proxy?.Url,
            MainUiOptions(sp.GetRequiredService<RuntimeTimeouts>()),
            WebAuthProbe.Script,
            HostLog.Write));
        services.AddSingleton(sp => new RuntimeSupervision(
            bootstrap,
            sp.GetRequiredService<IRecoverySurface>(),
            sp.GetRequiredService<IMintEpoch>(),
            sp.GetRequiredService<IWebViewNavigator>(),
            proxy?.Url,
            sp.GetRequiredService<UiLocale>(),
            SupervisionOptions(sp.GetRequiredService<RuntimeTimeouts>()),
            HostLog.Write));
        // 无边框顶栏注入用例（ADR frameless-uniform-caption-bar + B3）：脚本构建与三路注入全在用例内。
        services.AddSingleton(sp => new CaptionBarInstaller(
            sp.GetRequiredService<CurrentWindowAccessor>(),
            sp.GetRequiredService<IRynWebView>(),
            sp.GetRequiredService<RynNavigationCallbacks>(),
            sp.GetRequiredService<CaptionBarOptions>(),
            sp.GetRequiredService<UiLocale>(),
            sp.GetRequiredService<CancellationTokenSource>()));
        // 启动期告知用例（ADR shared-home-desktop-profile + B3）：用例编排住 Infrastructure，展示面闭包与
        // 脏退标志在注册处装配；宿主惰性解析，调用点晚于编排首个 HostSetup 解析，marker 语义不变。
        services.AddSingleton(sp =>
        {
            HostSetup host = sp.GetRequiredService<HostSetup>();
            CurrentWindowAccessor accessor = sp.GetRequiredService<CurrentWindowAccessor>();
            UiLocale uiLocale = sp.GetRequiredService<UiLocale>();
            return new StartupNoticeService(
                bootstrap,
                TimeSpan.FromSeconds(sp.GetRequiredService<RuntimeTimeouts>().BootstrapSettleTimeoutSeconds),
                host.Marker.PreviousRunUnclean,
                HostLog.Write,
                (version, ct) => PagePump.ShowBannerWhenReadyAsync(
                    accessor, DesktopBanner.BuildVersionFloorBanner(version, uiLocale), ct),
                ct => PagePump.ShowBannerWhenReadyAsync(
                    accessor, DesktopBanner.BuildUncleanExitBanner(uiLocale), ct));
        });
        AddExitOrchestration(services, bootstrap);
        // 排序器运行期解析面（ADR sequencer-resolution-injection）：两个基础设施用例为实例单例
        // （原静态入口是 R1b 静态调用面），九项运行期单例以 Lazy 携带——Value 落在原解析点，
        // 保「宿主首解即建宿主、晚于 profile 前置」与「窗口就绪后才碰页面面」两处时点。
        services.AddSingleton<ProfileLifecycle>();
        services.AddSingleton<CompanionPreSpawn>();
        services.AddSingleton(sp => new RuntimeResolutions(
            sp.GetRequiredService<ProfileLifecycle>(),
            sp.GetRequiredService<CompanionPreSpawn>(),
            new Lazy<HostSetup>(() => sp.GetRequiredService<HostSetup>()),
            new Lazy<Core.Bootstrap.RuntimeSupervision>(
                () => sp.GetRequiredService<Core.Bootstrap.RuntimeSupervision>()),
            new Lazy<ExitPipeline>(() => sp.GetRequiredService<ExitPipeline>()),
            new Lazy<Core.Ui.MainUiEntry>(() => sp.GetRequiredService<Core.Ui.MainUiEntry>()),
            new Lazy<CaptionBarInstaller>(() => sp.GetRequiredService<CaptionBarInstaller>()),
            new Lazy<StartupNoticeService>(() => sp.GetRequiredService<StartupNoticeService>()),
            new Lazy<PageHealthMonitor>(() => sp.GetRequiredService<PageHealthMonitor>()),
            new Lazy<Tray.TrayController>(() => sp.GetRequiredService<Tray.TrayController>()),
            new Lazy<TrayService>(() => sp.GetRequiredService<TrayService>())));
        return services;
    }

    /// <summary>退出编排用例登记（ADR app-exit-orchestration-core-usecase；join 预算按配置模型投影）。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="bootstrap">首启引导服务（退出时取消未落定的引导任务）。</param>
    private static void AddExitOrchestration(IServiceCollection services, IFirstBootBootstrap bootstrap) =>
        services.AddSingleton(sp => new Core.Bootstrap.AppExitOrchestration(
            sp.GetRequiredService<CancellationTokenSource>(),
            bootstrap,
            TimeSpan.FromSeconds(sp.GetRequiredService<RuntimeTimeouts>().SupervisorJoinTimeoutSeconds),
            sp.GetRequiredService<Core.Recovery.IDialogOrphanReaper>().Reap,
            HostLog.Write));

    /// <summary>进入主界面用例的时限快照（配置模型 → Core 侧记录）。</summary>
    /// <param name="timeouts">运行时超时家（容器单例）。</param>
    /// <returns>用例时限记录。</returns>
    private static MainUiEntryOptions MainUiOptions(RuntimeTimeouts timeouts) => new(
        timeouts.WindowReadyTimeoutSeconds,
        timeouts.WindowReadyPollIntervalSeconds,
        timeouts.AuthProbeTimeoutSeconds,
        timeouts.AuthProbeAttempts,
        timeouts.NavCallTimeoutSeconds,
        timeouts.NavCommitTimeoutSeconds);

    /// <summary>监督用例的时限快照（配置模型 → Core 侧记录）。</summary>
    /// <param name="timeouts">运行时超时家（容器单例）。</param>
    /// <returns>用例时限记录。</returns>
    private static RuntimeSupervisionOptions SupervisionOptions(RuntimeTimeouts timeouts) => new(
        timeouts.SupervisorRestartTimeoutSeconds,
        timeouts.SupervisorRecoveredRetryDelaySeconds,
        timeouts.SupervisorFailedRetryDelaySeconds,
        timeouts.SupervisorBlockedLogEveryRounds,
        timeouts.SupervisorFailureSurfaceStuckThreshold);
}
