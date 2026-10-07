using Microsoft.Extensions.DependencyInjection;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动编排服务（ADR compose-root-form-separation）：阶段主链与用例编排自组合根整体搬迁至此
/// （语句序/分支/异常边界逐一对应，纯搬家零行为变更）；组合根只装配与触发（R1）。Ryn 无 hosted-service
/// 机制，编排器由组合根在 Build 后显式组装并触发（结构必然，见 ADR Alternatives）。
/// 跨阶段值经构造与运行期单例双通道供给（单例在调用点幂等解析，见 Run/ShowTray/SetupSupervisor）；阶段产出为正式类型（<see cref="Preflight"/> 等）。
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
    private readonly PrimaryListener? _instanceListener;
    // 运行时起步用例（ADR 组合根机制收官先行批）：spawn→铸币→收 URL 下沉 Core 可单测；
    // 缺省实现以本编排器既有字段组装（测试经构造注入 fake）。
    private readonly IRuntimeStarter _runtimeStarter;
    // 恢复周期起点（恢复屏展示时刻写入、收养导航读出：周期内有导航到达即页内已自刷）。
    private DateTimeOffset _lastRecoveryShownAtUtc;
    // 「首次导航到达补注册顶栏脚本」的一次性闩（见 StartupSequence.Supervision 的 SetupCaptionBar）。
    private int _captionBarReregistered;

    /// <summary>创建编排服务（构造数据注入；由组合根在 Build 后显式组装，容器只给零件）。
    /// 运行期单例（宿主/令牌源/托盘）在调用点经应用容器解析——组装时点必晚于 Build，届时求值安全。</summary>
    public StartupSequence(
        Preflight preflight,
        AppSetup app,
        UpdateSetup updates,
        UiLocale uiLocale,
        RuntimeTimeouts timeouts,
        CaptionBarOptions captionBar,
        DshShellForward shellForward,
        DshLoopbackProxy? proxy,
        PrimaryListener? instanceListener,
        IRuntimeStarter? runtimeStarter = null)
    {
        _preflight = preflight;
        _app = app;
        _updates = updates;
        _uiLocale = uiLocale;
        _timeouts = timeouts;
        _captionBar = captionBar;
        _shellForward = shellForward;
        _proxy = proxy;
        _instanceListener = instanceListener;
        _runtimeStarter = runtimeStarter ?? new RuntimeStarter(
            _preflight.Bootstrap,
            TimeSpan.FromSeconds(_timeouts.SpawnTimeoutSeconds),
            _shellForward.MintAsync,
            HostLog.Write);
    }

    /// <summary>组合根入口：按原 <c>Program.Main</c> 语句序执行全部编排并返回进程退出码。
    /// 宿主/令牌源寿命由组合根拥有（构造注入），此处不释放。</summary>
    public int Run()
    {
        // profile 前置（migrate→recover→ensure→reconcile 用例编排住 Infrastructure.ProfileLifecycle）
        ProfileLifecycle.EnsureReady();
        // 宿主装配产出（容器惰性单例）：首次解析即创建；必须先于随包插件安装与 spawn（偏序 = 宿主已建、spawn 前），
        // 偏序在本方法内自洽，不依赖组合根的 eager 解析兜底。
        HostSetup host = _app.App.Services.GetRequiredService<HostSetup>();
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

    private void StartRuntime(HostSetup host)
    {
        // 用例下沉 Core（ADR 组合根机制收官先行批）：shim 注册→spawn 等 URL→壳铸币由 IRuntimeStarter
        // 承载、可单测（实现见 Core.Bootstrap.RuntimeStarter，搬运前语句逐句等价）。
        // 同步阻塞点仍在本方法：Run 链整体 async 化待 Ryn 线程模型审查，不在本批。
        _ = _runtimeStarter.StartAsync(host.Host, CancellationToken.None).GetAwaiter().GetResult();

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
        // 托盘在组合根装配期构造（容器单例），ShowTray 必然晚于它
        Tray.TrayController tray = _app.App.Services.GetRequiredService<TrayController>();
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
        // once-guard 使已回收路径的重复调用为 no-op（退出管道由容器单例供给）
        _app.App.Services.GetRequiredService<ExitPipeline>().ReapRuntime();
        // 非 orderly 退出路径也要释放单实例锁地址：orderly 路径已 Dispose 过，幂等守卫保证此处安全
        _instanceListener?.Dispose();
        return 0;
    }
}
