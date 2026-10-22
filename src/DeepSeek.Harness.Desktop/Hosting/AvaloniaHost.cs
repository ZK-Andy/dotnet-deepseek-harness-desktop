using Avalonia;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>宿主入口（ADR implemented/architecture/2026-10-22-avalonia-g0-entry-switch、-g1-tray-recall）：
/// Avalonia 窗口 + 包自带 WebView 承接既有 dsh spawn 与回环代理源；托盘、关窗到托盘、二启唤回随 G1
/// 首批接入。无边框顶栏、<c>window.__shell</c> 桥、监督器与恢复面留 G1 其余片与 G2–G3，Ryn 装配面保留可回退。
/// 装配面成员（C2）：本文件只许 <c>Run</c>；新增成员须先在 <c>verify-compose-root.py</c> 列册。</summary>
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
            (ProfileLifecycle profile, CompanionPreSpawn companion) = PreBuildRegistration.CreateStartupPrerequisites(shared.Timeouts);
            startup = new ShellStartup(
                async ct =>
                {
                    // 偏序与旧编排一致：profile 就绪 → 随包插件安装 → spawn；均在后台相执行。
                    profile.EnsureReady();
                    companion.EnsureInstalled(preflight.Bootstrap.IsNeeded, preflight.Launch);
                    await starter.StartAsync(host, ct).ConfigureAwait(false);
                },
                proxy.Proxy?.Url);
            // 托盘图标存在性是装配期配置探测（与旧入口 DesktopAdapter.App 同款，D005 显式豁免）
            string trayIconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
            bool trayIconAvailable = File.Exists(trayIconPath); // verify-code-conventions: ignore 装配面：托盘图标存在性是配置探测，非业务直调
            return AppBuilder.Configure(() => new ShellApplication(
                    startup, preflight.Launch.DevTools, recall, shared.UiLocale, trayIconPath, trayIconAvailable))
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // 先停后台起步（取消 + 有限 join），再按序释放进程级资源（host → proxy → cts），最后清崩溃 marker。
            try
            {
                startup?.Dispose();
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
}
