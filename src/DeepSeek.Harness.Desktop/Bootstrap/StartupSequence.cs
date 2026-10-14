using Microsoft.Extensions.DependencyInjection;
using Ryn.Plugins.Tray;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>
/// 启动编排服务（ADR compose-root-form-separation）：阶段主链与用例编排自组合根整体搬迁至此
/// （语句序/分支/异常边界逐一对应，纯搬家零行为变更）；组合根只装配与触发（R1）。Ryn 无 hosted-service
/// 机制，编排器由组合根在 Build 后解析并触发（结构必然，见 ADR Alternatives）。
/// 形状＝排序器：<see cref="Run"/> 只按序触发用例/阶段（无分支、无域文本、不构造服务——机器闸见
/// <c>verify-compose-root.py</c> C6），用例与依赖经容器解图供给（ADR 组合根机制收官终局的 B3）；
/// 阶段产出为正式类型（<see cref="Preflight"/> 等），运行期单例在调用点解析。
/// 本文件承载启动主链与产生段；监督/退出接线在 <c>StartupSequence.Supervision.cs</c>。
/// 导航/监督/顶栏注入/启动期告知已随用例下沉（ADR 组合根机制收官 step-1 的 B1/B2 与终局的 B3：
/// <c>Core.Ui.MainUiEntry</c> + <c>Core.Bootstrap.RuntimeSupervision</c> + <c>PageBridge.CaptionBarInstaller</c>
/// + <c>Infrastructure.Bootstrap.StartupNoticeService</c>）。
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

    /// <summary>创建编排服务（构造参数经容器解图，ADR 组合根机制收官终局的 B3）：共享态与用例依赖由
    /// 容器供给，接线层不再逐个手传；唯一例外是 <see cref="AppSetup"/>——Ryn 不把 <c>RynApplication</c>
    /// 登记进容器（<c>RynApplicationBuilder.Build()</c> 只返回实例），Build 后才存在的应用产出装不进解图，
    /// 由接线层解析时显式供给。运行期单例（宿主/令牌源/托盘）在调用点经容器解析。</summary>
    /// <param name="app">应用装配产出（Build 后唯一装不进容器的值，显式供给）。</param>
    /// <param name="preflight">容器之前的头部产出（引导服务与启动配置）。</param>
    /// <param name="updates">自更新栈装配产出（协调器已装载）。</param>
    /// <param name="instanceListener">单实例监听器持有（编排尾部释放锁地址）。</param>
    /// <param name="runtimeStarter">运行时起步用例（spawn→铸币→收 URL）。</param>
    /// <param name="timeouts">运行时超时家（配置模型单例；编排期留痕与 join 预算消费）。</param>
    public StartupSequence(
        AppSetup app,
        Preflight preflight,
        UpdateSetup updates,
        InstanceListenerSetup instanceListener,
        IRuntimeStarter runtimeStarter,
        RuntimeTimeouts timeouts)
    {
        _app = app;
        _preflight = preflight;
        _updates = updates;
        _instanceListener = instanceListener.Listener;
        _runtimeStarter = runtimeStarter;
        _timeouts = timeouts;
    }

    /// <summary>启动编排入口（排序器主链）：按语句序触发全部用例并返回进程退出码。
    /// 宿主/令牌源寿命由接线层拥有（容器单例），此处不释放。</summary>
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
        InstallCaptionBar();
        StartUpdateCheck();
        StartupNoticeTask(supervisor);
        return RunAppLoop(supervisor);
    }

    private void StartRuntime(HostSetup host)
    {
        // 用例下沉 Core（ADR 组合根机制收官先行批）：shim 注册→spawn 等 URL→壳铸币由 IRuntimeStarter
        // 承载、可单测（实现见 Core.Bootstrap.RuntimeStarter，搬运前语句逐句等价）。
        // 同步阻塞点仍在本方法：Run 链整体 async 化待 Ryn 线程模型审查，不在本批。
        _ = _runtimeStarter.StartAsync(host.Host, CancellationToken.None).GetAwaiter().GetResult();

        // dsh web URL 就此收官：下游（监督器/健康面）各自从宿主或回调取得 URL，
        // 无跨阶段消费点——不设阶段产出（死载荷会被 CompositionRootSequenceTests 拦下）。
    }

    private void RunBootstrapIfNeeded()
    {
        // 首启引导（ADR online-first-unbundled-runtime）：窗口先亮（wwwroot 引导页），引导服务后台完成
        // 检测/下载/安装/验证后起 dsh，并把就位 URL 交回调接回壳侧导航；失败推错误态等待用户重试
        // （desktop.bootstrap.retry 经闸门放行）。引导未落定前监督器/插件安装均被门控——依赖序即插入位。
        // 就位后的铸币/等窗/终页裁决是 Core 用例（ADR 组合根机制收官 step-1 的 B1）。
        Core.Ui.MainUiEntry entry = _app.App.Services.GetRequiredService<Core.Ui.MainUiEntry>();
        _preflight.Bootstrap.Start((url, ct) => entry.RunAsync(url, ct));
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
