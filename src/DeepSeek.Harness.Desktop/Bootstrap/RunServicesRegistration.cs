using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>运行期单例注册（ADR 组合根机制收官 step-2a：接线槽删除；2b：根字段归零）：宿主 + 崩溃标记、
/// 退出管道、页面健康观测、装配期共享态的生命周期还给容器。惰性工厂首次解析（编排 Run 期）即创建，
/// 与原编排期创建时序等价；实例单例（装配期已创建）在注册期只登记实例；路由闭包在调用期求值，注册期只存委托。</summary>
internal static class RunServicesRegistration
{
    /// <summary>装配期共享态（单例，2b 根字段归零）：一次性配置（超时/标题栏参数）与跨阶段共享单例
    /// （语言单点/壳转发器）。实例由组合根 <c>Run</c> 方法局部创建（前 Build 消费同一实例），此处只登记
    /// 实例供编排期经容器解析——单实例唯一，每加一个功能不再加一个根字段。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="timeouts">运行时超时家（与 A 类启动配置同点解析一次）。</param>
    /// <param name="captionBar">自绘标题栏参数家（ADR frameless-uniform-caption-bar）。</param>
    /// <param name="uiLocale">宿主 UI 语言单点（ADR host-ui-locale）。</param>
    /// <param name="shellForward">壳转发器（铸币态唯一家）。</param>
    public static IServiceCollection AddBootstrapSharedState(
        this IServiceCollection services,
        RuntimeTimeouts timeouts,
        CaptionBarOptions captionBar,
        UiLocale uiLocale,
        DshShellForward shellForward)
    {
        services.AddSingleton(timeouts);
        services.AddSingleton(captionBar);
        services.AddSingleton(uiLocale);
        services.AddSingleton(shellForward);
        return services;
    }

    /// <summary>宿主装配产出（单例）：运行时宿主 + 崩溃取证 marker。原编排 <c>SetupHostAndMarker</c>
    /// 语义——遗留 marker 即判上轮非受控退出；正常退出路径在退出管道清除。释放由组合根拥有
    /// （单例寿命 = 本次 Run，见 <c>DesktopAdapter.Run</c> finally）。</summary>
    public static IServiceCollection AddRunHost(this IServiceCollection services)
    {
        // HostSetup 是 readonly record struct（值流阶段产出，见 StartupStages.cs），无 class 约束的
        // 泛型单例重载收不了——用 Type 重载注册，解析面同样经 GetRequiredService<HostSetup>。
        services.AddSingleton(typeof(HostSetup), _ =>
        {
            // 全局 dsh 模型：宿主恒以 PATH dsh（bundled=null）形态运行（ADR simple-shell-single-global-dsh）。
            var host = new HarnessRuntimeHost(HostLog.Write);

            // 崩溃取证 marker（ADR shell-observability-diagnostics）：遗留即判定上轮非受控退出，
            // 正常退出路径在退出管道清除；脏退播报由首次解析点负责（工厂只做构造，见调用方）。
            RunMarkerResult marker = RunMarker.Acquire(HarnessRuntimeHost.ResolveDshHome());

            return new HostSetup(host, marker);
        });
        return services;
    }

    /// <summary>单实例退出管道（单例）：有序退出步骤即构造数据，托盘有序退出与 Run 尾部共用同一实例，
    /// 幂等由 once-guard 保证；运行时回收先于关窗。关窗动作延迟到调用期经容器解析
    /// （注册期窗口不存在；调用点必在窗口就绪之后，与原接线注释同理）。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="supervisorCts">监督器取消令牌源（装配期创建，寿命 = 本次 Run）。</param>
    /// <param name="instanceListener">单实例监听器（仲裁成功时持有；退出时释放地址）。</param>
    public static IServiceCollection AddExitPipeline(
        this IServiceCollection services, CancellationTokenSource supervisorCts, PrimaryListener? instanceListener)
    {
        services.AddSingleton(sp =>
        {
            HostSetup host = sp.GetRequiredService<HostSetup>();
            return new ExitPipeline(
                supervisorCts.Cancel,
                host.Host.Stop,
                () => RunMarker.Release(HarnessRuntimeHost.ResolveDshHome(), host.Marker.Token),
                () => instanceListener?.Dispose(),
                () => sp.GetRequiredService<IRynWindow>().Close(),
                log: HostLog.Write);
        });
        return services;
    }

    /// <summary>页面健康观测（单例）：宿主只读探针轮询 + 有界恢复（ADR page-health-monitor）。
    /// 启动由编排 <c>SetupHealthMonitor</c> 经解析触发；诊断快照消费方在调用期解析。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="proxy">回环代理源（可空；绑定失败时 reload 退化为 no-op）。</param>
    public static IServiceCollection AddHealthMonitor(this IServiceCollection services, DshLoopbackProxy? proxy)
    {
        services.AddSingleton(sp =>
        {
            CurrentWindowAccessor accessor = sp.GetRequiredService<CurrentWindowAccessor>();
            return new PageHealthMonitor(
                accessor,
                HostLog.Write,
                reload: ct => proxy is null
                    ? ValueTask.CompletedTask
                    : accessor.Current.NavigateAsync(proxy.Url, ct));
        });
        return services;
    }
}
