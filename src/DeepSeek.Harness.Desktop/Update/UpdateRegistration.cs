using Microsoft.Extensions.DependencyInjection;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Update;

/// <summary>自更新域命令路由注册（组合根注册下沉为按域 AddXxx，ADR compose-root-form-separation）。</summary>
internal static class UpdateRegistration
{
    /// <summary>自更新命令：desktop.update.getState / check / install（dev 门禁下不注册路由，invoke 自然失败）。
    /// 后台取消令牌在调用期经容器解析（监督器令牌源寿命 = 本次 Run，调用点必在其作用域内）。</summary>
    public static IServiceCollection AddUpdateCommands(this IServiceCollection services, UpdateSetup updates)
    {
        if (updates.Updates.Machine is { } updateMachine)
        {
            services.AddSingleton<ICommandRouter>(sp => new DesktopUpdateCommandRouter(
                updateMachine,
                log: HostLog.Write,
                backgroundToken: () => sp.GetRequiredService<CancellationTokenSource>().Token));
        }

        return services;
    }
}
