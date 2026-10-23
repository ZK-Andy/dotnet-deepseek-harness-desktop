using Avalonia;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>宿主入口（ADR implemented/architecture/2026-10-22-avalonia-g0-entry-switch、-g1-tray-recall、-g2-shell-bridge）：
/// Avalonia 窗口 + 包自带 WebView 承接既有 dsh spawn 与回环代理源；托盘、关窗到托盘、二启唤回、
/// 无边框顶栏（三平台原生拖拽/缩放，Wayland 降级）与启动失败面已接入；<c>window.__shell</c> 页面桥
/// 含外链拦截与页面健康观测。恢复页与监督器随 G1 余项/G3，Ryn 装配面保留可回退。
/// 装配面成员（C2）：本文件只许 <c>Run</c>、<c>RunUi</c>、<c>CreateStartup</c>；新增成员须先在
/// <c>verify-compose-root.py</c> 的 ALLOWED_METHODS 列册（本片为拆 F2 方法长度上限而拆分启动面）。</summary>
internal static class AvaloniaHost
{
    /// <summary>启动 Avalonia 主循环：预检与单实例仲裁后开窗，dsh spawn 与铸币在后台相完成，页面经壳 origin 加载。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>进程退出码（Avalonia lifetime 的退出码；已有主实例时为 0）。</returns>
    public static int Run(string[] args)
    {
        // WebKit 沙箱 userns 降级必须先于一切 WebView 创建（renderer 继承本进程 env）。
        WebkitSandboxFallback.Apply();

        ShellResources resources = PreBuildRegistration.CreateShellResources();
        RunMarkerResult? marker = null;
        ShellStartup? startup = null;
        ShellBridgeHost? bridgeHost = null;
        try
        {
            SharedState shared = PreBuildRegistration.CreateSharedState(static () => null);
            var host = new HarnessRuntimeHost(HostLog.Write, shared.Timeouts);
            resources.Track(host);

            // 启动前置（首启探测与 dev 判定）先于代理与窗口；探测上限由 RuntimeTimeouts 约束。
            Preflight preflight = PreBuildRegistration.CreatePreflight(
                () => host, () => shared.UiLocale.IsEnglish, shared);

            // 单实例仲裁：只防第二实例冷启动清扫并击杀第一实例的 dsh；二启通知经唤回单点转给主窗，
            // 建窗前的请求由 ShellRecall 挂起、窗口就绪后补发。
            var recall = new ShellRecall();
            if (!PreBuildRegistration.TryBecomePrimary(
                    preflight, shared.Timeouts, () => { recall.Request(); return Task.CompletedTask; }, out PrimaryListener? instanceListener))
            {
                return 0;
            }

            resources.Attach(instanceListener);

            // 崩溃 marker 晚于单实例仲裁：第二实例若先 Acquire 会覆盖主实例 marker 并在退出时删掉它，
            // 主实例的「非受控退出」取证就被抹掉（旧入口在仲裁后才由 HostSetup 工厂 Acquire）。
            marker = RunMarker.Acquire(HarnessRuntimeHost.ResolveDshHome());

            // 回环代理源：绑定失败 loud 后降级为空窗（G0 不实现 wwwroot 降级，见 ShellWindow 注释）。
            DshLoopbackProxy.ProxySetup proxy = DshLoopbackProxy.TryCreate(
                shared.ShellForward, Path.Combine(AppContext.BaseDirectory, "wwwroot"), HostLog.Write);
            resources.Attach(proxy);

            IRuntimeStarter starter = PreBuildRegistration.CreateRuntimeStarter(
                preflight, shared.Timeouts, shared.ShellForward);

            // 窗口加载壳 origin（代理）：dsh 就绪前代理按铸币等待预算转引导页，dsh 起后同一 origin 直达 UI。
            startup = CreateStartup(preflight, shared, host, starter, proxy);
            // 托盘图标存在性是装配期配置探测（与旧入口 DesktopAdapter.App 同款，D005 显式豁免）
            string trayIconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
            bool trayIconAvailable = File.Exists(trayIconPath); // verify-code-conventions: ignore 装配面：托盘图标存在性是配置探测，非业务直调

            // 页面桥（ADR avalonia-g2-shell-bridge）：命令面先接 PageBridge 域四路由，其余随 G3；
            // 代理未绑定时 origin 为 null，桥与健康观测整体降级跳过（ShellBridgeHost 自持该判据）。
            bridgeHost = PageBridgeRegistration.CreateShellBridgeHost(
                proxy.Proxy?.Url?.GetLeftPart(UriPartial.Authority),
                shared.UiLocale,
                MacNativeChrome.IsEnabled,
                shared.CaptionBar.HeightPx,
                TimeSpan.FromSeconds(shared.Timeouts.HealthInitialDelaySeconds),
                HostLog.Write);

            return RunUi(
                args, startup, preflight.Launch.DevTools, recall, shared.UiLocale, trayIconPath, trayIconAvailable,
                bridgeHost, () => host.StderrTail);
        }
        finally
        {
            // 先停后台起步（取消 + 有限 join），再按序释放进程级资源（host → proxy → cts），最后清崩溃 marker。
            try
            {
                startup?.Dispose();
                bridgeHost?.Dispose();
                resources.Dispose();
            }
            finally
            {
                if (marker is RunMarkerResult acquired)
                {
                    RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), acquired.Token);
                }
            }
        }
    }

    /// <summary>后台起步体（profile 就绪 → 随包插件安装 → spawn；均在后台相执行）。</summary>
    /// <param name="preflight">启动前置产出。</param>
    /// <param name="shared">装配期共享态（超时家）。</param>
    /// <param name="host">运行时宿主。</param>
    /// <param name="starter">运行时起步用例。</param>
    /// <param name="proxy">回环代理源（窗口初始 URL 来源）。</param>
    /// <returns>起步壳。</returns>
    private static ShellStartup CreateStartup(
        Preflight preflight,
        SharedState shared,
        HarnessRuntimeHost host,
        IRuntimeStarter starter,
        DshLoopbackProxy.ProxySetup proxy)
    {
        (ProfileLifecycle profile, CompanionPreSpawn companion) = PreBuildRegistration.CreateStartupPrerequisites(shared.Timeouts);
        return new ShellStartup(
            async ct =>
            {
                profile.EnsureReady();
                companion.EnsureInstalled(preflight.Bootstrap.IsNeeded, preflight.Launch);
                await starter.StartAsync(host, ct).ConfigureAwait(false);
            },
            proxy.Proxy?.Url);
    }

    /// <summary>开 AvaLonia 主循环（窗口、托盘与页面桥都在 <see cref="ShellApplication"/> 里接线）。</summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="startup">运行时起步壳。</param>
    /// <param name="devTools">是否开启 WebView 调试器。</param>
    /// <param name="recall">唤回单点。</param>
    /// <param name="uiLocale">UI 语言单点。</param>
    /// <param name="trayIconPath">托盘图标路径。</param>
    /// <param name="trayIconAvailable">托盘图标是否存在。</param>
    /// <param name="bridgeHost">页面桥装配。</param>
    /// <param name="stderrTail">子进程 stderr 尾部取法（启动失败面用）。</param>
    /// <returns>进程退出码。</returns>
    private static int RunUi(
        string[] args,
        ShellStartup startup,
        bool devTools,
        ShellRecall recall,
        UiLocale uiLocale,
        string trayIconPath,
        bool trayIconAvailable,
        ShellBridgeHost bridgeHost,
        Func<IReadOnlyList<string>> stderrTail) =>
        AppBuilder.Configure(() => new ShellApplication(
                startup, devTools, recall, uiLocale, trayIconPath, trayIconAvailable, bridgeHost, stderrTail))
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
}
