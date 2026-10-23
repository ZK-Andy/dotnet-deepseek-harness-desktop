namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// 运行期接线的装配（ADR implemented/architecture/2026-10-23-avalonia-g1g3-recovery-bootstrap-supervision）：
/// 起步壳、监督器令牌源、退出管道、页面桥与恢复上下文一次接线，供 <see cref="AvaloniaHost.Run"/> 取用。
/// 拆出根文件是 C2 的方向要求（根只许在册成员）；本类只按序调用注册处产物，不含启动判据。
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
        ShellResources resources)
    {
        ShellStartup startup = AvaloniaHost.CreateStartup(preflight, shared, host, starter, proxy);
        // 监督器取消令牌源（寿命 = 本次 Run）：退出管道、恢复面与监督循环共用；释放归 ShellResources。
        var supervisorCts = new CancellationTokenSource();
        resources.Attach(supervisorCts);
        var recovery = new ShellRecoveryContext(
            preflight.Bootstrap, host, supervisorCts, shared.ShellForward, shared.Timeouts, HostLog.Write);
        // 退出/重启管道（ADR avalonia-g3-exit-restart）：回收三步与关窗动作即构造数据；
        // 托盘菜单、desktop.app.restart 与恢复页共用同一实例（幂等由管道内 once-guard 保证）。
        ExitPipeline exitPipeline = AvaloniaHost.CreateLifetime(
            host, marker, instanceListener, dshHome, recovery.CancelAndJoin);

        // 页面桥（ADR avalonia-g2-shell-bridge）：代理未绑定时 origin 为 null，
        // 桥与健康观测整体降级跳过（ShellBridgeHost 自持该判据）。
        ShellBridgeHost bridgeHost = PageBridgeRegistration.CreateShellBridgeHost(
            proxy.Proxy?.Url?.GetLeftPart(UriPartial.Authority),
            shared.UiLocale,
            MacNativeChrome.IsEnabled,
            shared.CaptionBar.HeightPx,
            TimeSpan.FromSeconds(shared.Timeouts.HealthInitialDelaySeconds),
            HostLog.Write);

        return new RunWiring(startup, bridgeHost, exitPipeline, recovery);
    }
}

/// <summary>运行期接线的产物（起步壳、页面桥、退出管道与恢复上下文）。</summary>
/// <param name="Startup">运行时起步壳。</param>
/// <param name="BridgeHost">页面桥装配。</param>
/// <param name="Exit">退出管道。</param>
/// <param name="Recovery">恢复/引导/监督依赖束。</param>
internal sealed record RunWiring(
    ShellStartup Startup,
    ShellBridgeHost BridgeHost,
    ExitPipeline Exit,
    ShellRecoveryContext Recovery);
