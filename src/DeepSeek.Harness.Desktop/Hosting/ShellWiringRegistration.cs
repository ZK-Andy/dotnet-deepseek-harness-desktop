using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// 运行期接线的装配（ADR implemented/architecture/2026-10-23-avalonia-g1g3-recovery-bootstrap-supervision）：
/// 起步壳、监督器令牌源、退出管道、页面桥与恢复上下文一次接线，供 <see cref="AvaloniaHost.Run"/> 取用。
/// 拆出根文件是 C2 的方向要求（根只许在册成员）；本类只按序调用注册处产物，不含启动判据。
/// 关窗闸门与自更新面同在此构造（ADR avalonia-g3-update-surface）：两者的寿命都随本次 Run，
/// 且都早于窗口——托盘命令、停用面与退出管道共用同一实例。
/// </summary>
internal static class ShellWiringRegistration
{
    /// <summary>装配运行期接线（调用方保证 preflight/host/proxy 已就绪）。</summary>
    /// <param name="preflight">启动前置产出（dev 判定、引导与铸币归属）。</param>
    /// <param name="shared">装配期共享态（超时家、语言与铸币转发器）。</param>
    /// <param name="host">运行时宿主。</param>
    /// <param name="starter">运行时起步用例。</param>
    /// <param name="proxy">回环代理源（窗口初始 URL 与桥 origin 来源）。</param>
    /// <param name="marker">本轮 run-marker（退出管道释放时校验 owner token）。</param>
    /// <param name="instanceListener">单实例监听器（Windows/降级为 null）。</param>
    /// <param name="dshHome">DSH home（marker 根）。</param>
    /// <param name="resources">进程级资源拥有者（监督器令牌源在此登记）。</param>
    /// <param name="recall">唤回单点（自更新结论的可见化落点）。</param>
    /// <returns>接线产物。</returns>
    public static RunWiring Create(
        Preflight preflight,
        SharedState shared,
        HarnessRuntimeHost host,
        IRuntimeStarter starter,
        DshLoopbackProxy.ProxySetup proxy,
        RunMarkerResult marker,
        PrimaryListener? instanceListener,
        string dshHome,
        ShellResources resources,
        ShellRecall recall)
    {
        ShellStartup startup = AvaloniaHost.CreateStartup(preflight, shared, host, starter, proxy);
        // 监督器取消令牌源（寿命 = 本次 Run）：退出管道、恢复面与监督循环共用；释放归 ShellResources。
        var supervisorCts = new CancellationTokenSource();
        resources.Attach(supervisorCts);
        var recovery = new ShellRecoveryContext(
            preflight.Bootstrap, host, supervisorCts, shared.ShellForward, shared.Timeouts, HostLog.Write);
        // 关窗闸门先于窗口与自更新面（ADR avalonia-g3-shell-commands）：安装路径与托盘退出共用同一放行点。
        var closeGate = new CloseGate();
        // 退出/重启管道（ADR avalonia-g3-exit-restart、restart-proxy-port-release）：回收三步、端口释放与
        // 关窗动作即构造数据；托盘菜单、desktop.app.restart 与恢复页共用同一实例（幂等由管道内 once-guard 保证）。
        // 关窗动作同时交给自更新面：安装器派生后要关窗放行脚本等待环，两处不得各写一份投递。
        Action closeWindow = CloseWindowOnUiThread;
        ExitPipeline exitPipeline = AvaloniaHost.CreateLifetime(
            host, marker, resources.ReleaseLocalPorts, dshHome, recovery.CancelAndJoin, closeWindow);

        // 页面桥（ADR avalonia-g2-shell-bridge）：代理未绑定时 origin 为 null，
        // 桥与健康观测整体降级跳过（ShellBridgeHost 自持该判据）。
        ShellBridgeHost bridgeHost = PageBridgeRegistration.CreateShellBridgeHost(
            proxy.Proxy?.Url?.GetLeftPart(UriPartial.Authority),
            shared.UiLocale,
            MacNativeChrome.IsEnabled,
            shared.CaptionBar.HeightPx,
            TimeSpan.FromSeconds(shared.Timeouts.HealthInitialDelaySeconds),
            HostLog.Write);

        // 自更新面（ADR avalonia-g3-update-surface）：三条命令路由、启动检查与托盘反馈一次构造。
        UpdateSurface updates = UpdateSurfaceRegistration.Create(
            preflight.Launch.IsDev,
            () => supervisorCts.Token,
            bridgeHost.ScriptTarget,
            shared.Pump,
            shared.UiLocale,
            recall.Request,
            closeGate.ApproveExit,
            closeWindow,
            exitPipeline.ScheduleExitFallback,
            HostLog.Write);

        // 启动期告知（ADR shared-home-desktop-profile + avalonia-g3-update-surface 的落点判据）：
        // 版本底线与非受控退出两条横幅走「壳真 UI」判据——holder 页 location.reload() 会吞掉注入，
        // 等待与重试语义在 PagePump；落定握手取消即放弃。
        var notices = new StartupNoticeService(
            preflight.Bootstrap,
            TimeSpan.FromSeconds(shared.Timeouts.BootstrapSettleTimeoutSeconds),
            TimeSpan.FromSeconds(shared.Timeouts.VersionProbeTimeoutSeconds),
            marker.PreviousRunUnclean,
            HostLog.Write,
            (version, ct) => shared.Pump.ShowBannerWhenShellReadyAsync(
                bridgeHost.ScriptTarget,
                DesktopBanner.BuildVersionFloorBanner(version, shared.UiLocale),
                WebAuthRecovery.HolderMarker,
                ct),
            ct => shared.Pump.ShowBannerWhenShellReadyAsync(
                bridgeHost.ScriptTarget,
                DesktopBanner.BuildUncleanExitBanner(shared.UiLocale),
                WebAuthRecovery.HolderMarker,
                ct));

        return new RunWiring(startup, bridgeHost, exitPipeline, recovery, closeGate, updates, closeWindow, notices);
    }

    /// <summary>主窗关窗（UI 线程）：退出管道、自更新安装与恢复页退出共用同一份投递——后台线程调用时
    /// 投递过去；迟到调用（窗口已关）在投递内兜住并留痕，不让异常逃到 UI 线程。</summary>
    private static void CloseWindowOnUiThread() => Dispatcher.UIThread.Post(() =>
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        try
        {
            desktop.MainWindow?.Close();
        }
        catch (InvalidOperationException ex)
        {
            // 窗口已关闭：Avalonia 拒绝 Close；关窗链路不因迟到调用中断。
            HostLog.Write($"[host] 关窗投递失败：{ex.Message}");
        }
    });
}

/// <summary>运行期接线的产物（起步壳、页面桥、退出管道、恢复上下文、关窗闸门与自更新面）。</summary>
/// <param name="Startup">运行时起步壳。</param>
/// <param name="BridgeHost">页面桥装配。</param>
/// <param name="Exit">退出管道。</param>
/// <param name="Recovery">恢复/引导/监督依赖束。</param>
/// <param name="CloseGate">关窗闸门（失败面/托盘/恢复页与自更新安装共用的同一放行点）。</param>
/// <param name="Updates">自更新面（命令路由、启动检查与托盘「检查更新」动作）。</param>
/// <param name="CloseWindow">主窗关窗投递（退出管道、自更新安装与恢复页退出共用）。</param>
/// <param name="Notices">启动期告知（版本底线/非受控退出横幅；引导落定后由窗口起步收口触发）。</param>
internal sealed record RunWiring(
    ShellStartup Startup,
    ShellBridgeHost BridgeHost,
    ExitPipeline Exit,
    ShellRecoveryContext Recovery,
    CloseGate CloseGate,
    UpdateSurface Updates,
    Action CloseWindow,
    StartupNoticeService Notices);
