using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Recovery;

/// <summary>诊断/恢复域命令路由注册（组合根注册下沉为按域 AddXxx，ADR compose-root-form-separation）。</summary>
internal static class RecoveryRegistration
{
    /// <summary>诊断包导出（desktop.diagnostics.export；ryn.json 的 desktop 能力面已放行）。
    /// 健康快照在调用期经容器解析（调用点必在观测启动之后；观测未起时快照为 null）。</summary>
    public static IServiceCollection AddDiagnosticsCommands(this IServiceCollection services)
    {
        services.AddSingleton<ICommandRouter>(sp => new DesktopDiagnosticsCommandRouter(
            log: HostLog.Write,
            healthSnapshot: () => sp.GetRequiredService<PageHealthMonitor>().Snapshot));
        return services;
    }

    /// <summary>恢复页退出（desktop.recovery.exit）：先批准关窗闸门再 Close——hide-to-tray 拦截下
    /// 未批准的 Close 会吞成隐藏；顺序契约与托盘退出同款（ADR diag-masking-and-recovery-page）。
    /// 关窗闸门在调用期经容器解析（调用点必在编排接线之后）。</summary>
    public static IServiceCollection AddRecoveryCommands(this IServiceCollection services)
    {
        services.AddSingleton<ICommandRouter>(sp => new RecoveryCommandRouter(
            closeWindow: () => sp.GetRequiredService<IRynWindow>().Close(),
            sp.GetRequiredService<TrayController>().CloseGate,
            HostLog.Write));
        return services;
    }
}
