using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>运行期单例注册（ADR 组合根机制收官 step-2a：接线槽删除）：宿主 + 崩溃标记、退出管道、
/// 页面健康观测的生命周期还给容器。全部惰性工厂——首次解析（编排 Run 期）即创建，
/// 与原编排期创建时序等价；路由闭包在调用期求值，注册期只存委托。</summary>
internal static class RunServicesRegistration
{
    /// <summary>宿主装配产出（单例）：运行时宿主 + 崩溃取证 marker。原编排 <c>SetupHostAndMarker</c>
    /// 语义——遗留 marker 即判上轮非受控退出；正常退出路径在退出管道清除。释放由组合根拥有
    /// （单例寿命 = 本次 Run，见 <c>DesktopBootstrap.Run</c> finally）。</summary>
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
