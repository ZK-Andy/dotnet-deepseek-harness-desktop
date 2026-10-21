using Microsoft.Extensions.DependencyInjection;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>引导域命令路由注册（装配面注册下沉为按域 AddXxx，ADR compose-root-form-separation）：
/// 引导重试与应用重启两域（ADR shell-router-rehoming）。</summary>
internal static class BootstrapRegistration
{
    /// <summary>引导重试（desktop.bootstrap.retry，ADR online-first-unbundled-runtime）：wwwroot 引导页的
    /// 重试按钮 → 闸门放行引导循环。gate 实例在引导服务创建，引导任务与路由共用同一实例。</summary>
    public static IServiceCollection AddBootstrapCommands(this IServiceCollection services, IFirstBootBootstrap bootstrap)
    {
        services.AddSingleton<ICommandRouter>(new BootstrapCommandRouter(bootstrap.Gate, HostLog.Write));
        return services;
    }

    /// <summary>应用重启（desktop.app.restart，ADR app-restart-native-switch）：受理即回帧、
    /// 延迟冲刷窗口后先批准关窗闸门再走退出管道重启。管道与托盘控制器在调用期经容器解析，
    /// 命令必经页面交互触发，调用时点必已就绪（与 TrayRegistration 的接线注释同理）。</summary>
    public static IServiceCollection AddAppRestartCommand(this IServiceCollection services)
    {
        services.AddSingleton<ICommandRouter>(sp => new AppRestartCommandRouter(
            closeGate: sp.GetRequiredService<TrayController>().CloseGate,
            restart: () => sp.GetRequiredService<ExitPipeline>().Restart(AppRelaunch.SpawnSelf),
            log: HostLog.Write));
        return services;
    }
}
