using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Recovery;

/// <summary>诊断/恢复域命令路由注册（组合根注册下沉为按域 AddXxx，ADR compose-root-form-separation）。</summary>
internal static class RecoveryRegistration
{
    /// <summary>诊断包导出（desktop.diagnostics.export；ryn.json 的 desktop 能力面已放行）。
    /// <paramref name="healthSnapshot"/> 导出时刻求值页面健康快照（page-health-monitor 接线，惰性读 wiring）。</summary>
    public static IServiceCollection AddDiagnosticsCommands(this IServiceCollection services, Func<string?> healthSnapshot)
    {
        services.AddSingleton<ICommandRouter>(new DesktopDiagnosticsCommandRouter(
            log: HostLog.Write, healthSnapshot: healthSnapshot));
        return services;
    }

    /// <summary>恢复页退出（desktop.recovery.exit）：先批准关窗闸门再 Close——hide-to-tray 拦截下
    /// 未批准的 Close 会吞成隐藏；顺序契约与托盘退出同款（ADR diag-masking-and-recovery-page）。</summary>
    public static IServiceCollection AddRecoveryCommands(this IServiceCollection services, StartupWiring wiring)
    {
        services.AddSingleton<ICommandRouter>(sp => new RecoveryCommandRouter(
            closeWindow: () => sp.GetRequiredService<IRynWindow>().Close(),
            wiring.Tray!.CloseGate,
            HostLog.Write));
        return services;
    }
}
