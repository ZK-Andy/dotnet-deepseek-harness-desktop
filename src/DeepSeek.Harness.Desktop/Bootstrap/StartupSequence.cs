using Microsoft.Extensions.DependencyInjection;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动编排服务（ADR compose-root-form-separation）：阶段主链与用例编排自组合根整体搬迁至此
/// （语句序/分支/异常边界逐一对应，纯搬家零行为变更）；组合根只装配与触发（R1）。Ryn 无 hosted-service
/// 机制，编排器由组合根 Build 后经 <see cref="IStartupSequence"/> 显式触发（结构必然，见 ADR Alternatives）。
/// 长命惰性接线槽在 <see cref="StartupWiring"/>；阶段产出为正式类型（<see cref="Preflight"/> 等）。
/// 本文件承载启动主链与产生段；监督/退出接线在 <c>StartupSequence.Supervision.cs</c>，
/// 导航原语与窗口就绪等待在 <c>StartupSequence.Navigation.cs</c>，网页会话自愈在 <c>StartupSequence.WebSession.cs</c>。
/// </summary>
internal sealed partial class StartupSequence : IStartupSequence
{
    private readonly Preflight _preflight;
    private readonly AppSetup _app;
    private readonly UpdateSetup _updates;
    private readonly UiLocale _uiLocale;
    private readonly RuntimeTimeouts _timeouts;
    private readonly CaptionBarOptions _captionBar;
    private readonly DshShellForward _shellForward;
    private readonly DshLoopbackProxy? _proxy;
    private readonly StartupWiring _wiring;
    private readonly PrimaryListener? _instanceListener;
    // 「首次导航到达补注册顶栏脚本」的一次性闩（见 StartupSequence.Supervision 的 SetupCaptionBar）。
    private int _captionBarReregistered;

    /// <summary>创建编排服务（构造数据注入；由组合根在 Build 后经容器工厂解析）。</summary>
    public StartupSequence(
        Preflight preflight,
        AppSetup app,
        UpdateSetup updates,
        UiLocale uiLocale,
        RuntimeTimeouts timeouts,
        CaptionBarOptions captionBar,
        DshShellForward shellForward,
        DshLoopbackProxy? proxy,
        StartupWiring wiring,
        PrimaryListener? instanceListener)
    {
        _preflight = preflight;
        _app = app;
        _updates = updates;
        _uiLocale = uiLocale;
        _timeouts = timeouts;
        _captionBar = captionBar;
        _shellForward = shellForward;
        _proxy = proxy;
        _wiring = wiring;
        _instanceListener = instanceListener;
    }

    /// <summary>组合根入口：按原 <c>Program.Main</c> 语句序执行全部编排并返回进程退出码。</summary>
    public int Run()
    {
        try
        {
            // profile 前置（migrate→recover→ensure→reconcile 用例编排住 Infrastructure.ProfileLifecycle）
            ProfileLifecycle.EnsureReady();
            HostSetup host = SetupHostAndMarker();
            // 随包插件 spawn 前安装（策略谓词与安装住 Infrastructure.CompanionPreSpawn；偏序 = 宿主已建、spawn 前）
            CompanionPreSpawn.EnsureInstalled(_preflight.Bootstrap.IsNeeded, _preflight.Launch);
            StartRuntime(host);
            RunBootstrapIfNeeded();
            ShowTray();
            SupervisorSetup supervisor = SetupSupervisor(host);
            SetupHealthMonitor(supervisor);
            SetupCaptionBar(supervisor);
            StartUpdateCheck();
            StartupNoticeTask(host, supervisor);
            return RunAppLoop(supervisor);
        }
        finally
        {
            // 原 `using var host` / `using var supervisorCts` 作用域到 Run 末尾；编排搬迁后释放随编排走。
            // 代理资源不在此——代理在容器之前启动，释放留在组合根 Run 的 finally（ProxySetup.Dispose）。
            _wiring.Host?.Dispose();
            _wiring.SupervisorCts?.Dispose();
        }
    }

    /// <summary>宿主与崩溃标记创建（阶段产出）。</summary>
    private HostSetup SetupHostAndMarker()
    {
        // 原 `using var host`：生命周期由 Run 的 finally 释放（接线 wiring.Host，引导服务惰性读）。
        // 全局 dsh 模型：宿主恒以 PATH dsh（bundled=null）形态运行（ADR simple-shell-single-global-dsh）。
        HarnessRuntimeHost host = new(HostLog.Write);
        _wiring.Host = host;

        // 崩溃取证 marker（ADR shell-observability-diagnostics）：遗留即判定上轮非受控退出；
        // 正常退出路径在退出管道清除
        RunMarkerResult marker = RunMarker.Acquire(HarnessRuntimeHost.ResolveDshHome());
        if (marker.PreviousRunUnclean)
        {
            HostLog.Write("[host] 检测到上轮未正常退出的标记；如频繁出现请在设置页导出诊断信息");
        }

        return new HostSetup(host, marker);
    }

    private void StartRuntime(HostSetup host)
    {
        // CLI shim 注册（ADR simple-shell-single-global-dsh）：dsh 已全局在 PATH，仅注册 pnpm shim。
        // best-effort——注册内部吞预期异常（见 CliShimRegistrar），此处再兜底意外异常。
        _preflight.Bootstrap.RegisterCliShim();

        DshWebUrl? webUrl = _preflight.Bootstrap.IsNeeded
            ? null
            : DshWebUrl.FromNullable(host.Host.StartAsync(timeout: TimeSpan.FromSeconds(_timeouts.SpawnTimeoutSeconds)).GetAwaiter().GetResult());
        if (!_preflight.Bootstrap.IsNeeded)
        {
            HostLog.Write($"[host] runtime = {host.Host.RuntimeDescription}");
            if (webUrl is not null)
            {
                HostLog.Write($"[host] dsh web = {webUrl}");
            }
            else
            {
                HostLog.Write($"[host] dsh 未在时限内给出 URL；降级加载 wwwroot。stderr 尾巴：\n{string.Join('\n', host.Host.StderrTail.TakeLast(8))}");
            }
        }

        if (webUrl is not null)
        {
            // 壳铸币（对齐上游 authenticateWebHost）：窗口/导航一律走壳 origin，先铸后载；
            // 失败 loud，窗口照开（转发 401/502 → 探针/恢复面按错误页处理，不挡启动）。
            // 同步编排沿用既有 GetAwaiter 形态；上限由转发器内部超时兜底。
            _ = _shellForward.MintAsync(webUrl.Value, HostLog.Write, CancellationToken.None).GetAwaiter().GetResult();
        }

        // dsh web URL 就此收官：下游（监督器/收养导航）各自从宿主或回调取得 URL，
        // 无跨阶段消费点——不设阶段产出（死载荷会被 CompositionRootSequenceTests 拦下）。
    }

    private void RunBootstrapIfNeeded()
    {
        // 首启引导（ADR online-first-unbundled-runtime）：窗口先亮（wwwroot 引导页），引导服务后台完成
        // 检测/下载/安装/验证后起 dsh，并把就位 URL 交回调接回壳侧导航；失败推错误态等待用户重试
        // （desktop.bootstrap.retry 经闸门放行）。引导未落定前监督器/插件安装均被门控——依赖序即插入位。
        _preflight.Bootstrap.Start((url, ct) => EnterMainUiAsync(url, ct));
    }

    /// <summary>托盘就绪化（装配壳）：解析 Ryn 托盘服务并交给控制器（无托盘环境传 null）。</summary>
    private void ShowTray()
    {
        // 托盘在组合根装配期构造（wiring.Tray），ShowTray 必然晚于它
        Tray.TrayController tray = _wiring.Tray!;
        tray.Show(tray.IsAvailable
            ? () => _app.App.Services.GetRequiredService<TrayService>()
            : null);
    }

    private void StartUpdateCheck()
    {
        // 自更新启动对账 + 后台检查一次（失败静默转 error 态，不影响首屏）
        _updates.Updates.Start();
    }

    private int RunAppLoop(SupervisorSetup supervisor)
    {
        HostLog.Write("[host] Ryn Run 开始（阻塞直到窗口关闭）");
        try
        {
            _app.App.Run();
        }
        catch (Exception ex)
        {
            HostLog.Write($"[host] Ryn Run 异常：{ex}");
        }

        HostLog.Write("[host] Ryn Run 结束");
        supervisor.Cts.Cancel();
        _preflight.Bootstrap.Cancel();
        try
        {
            supervisor.Task.Wait(TimeSpan.FromSeconds(_timeouts.SupervisorJoinTimeoutSeconds));
        }
        catch (AggregateException)
        {
            // 监督任务随宿主回收而结束；无需上报
        }

        // 非 orderly 退出路径（用户直接关窗使 Run 返回）与托盘有序退出共用同一单实例管道：
        // once-guard 使已回收路径的重复调用为 no-op（退出管道在 SetupSupervisor 的 WireExitHandlers 接线）
        _wiring.Exit!.ReapRuntime();
        // 非 orderly 退出路径也要释放单实例锁地址：orderly 路径已 Dispose 过，幂等守卫保证此处安全
        _instanceListener?.Dispose();
        return 0;
    }
}
