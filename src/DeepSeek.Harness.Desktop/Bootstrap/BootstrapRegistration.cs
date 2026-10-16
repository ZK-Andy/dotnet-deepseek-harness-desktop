using Microsoft.Extensions.DependencyInjection;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Bootstrap;

/// <summary>引导域命令路由注册（组合根注册下沉为按域 AddXxx，ADR compose-root-form-separation）。</summary>
internal static class BootstrapRegistration
{
    /// <summary>引导重试（desktop.bootstrap.retry，ADR online-first-unbundled-runtime）：wwwroot 引导页的
    /// 重试按钮 → 闸门放行引导循环。gate 实例在引导服务创建，引导任务与路由共用同一实例。</summary>
    public static IServiceCollection AddBootstrapCommands(this IServiceCollection services, IFirstBootBootstrap bootstrap)
    {
        services.AddSingleton<ICommandRouter>(new BootstrapCommandRouter(bootstrap.Gate, HostLog.Write));
        return services;
    }
}
