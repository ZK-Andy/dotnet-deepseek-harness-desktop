using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Recovery;

/// <summary>诊断/恢复域命令路由注册（组合根注册下沉为按域 AddXxx，ADR compose-root-form-separation）。</summary>
internal static class RecoveryRegistration
{
    /// <summary>诊断包导出（desktop.diagnostics.export；ryn.json 的 desktop 能力面已放行）。
    /// 健康快照在调用期经容器解析（调用点必在观测启动之后；观测未起时快照为 null）。
    /// 同域登记失败面的自动导出端口（ADR native-failure-surface）：页面命令面与失败面共用同一导出器，
    /// 只是入口不同（静态基础设施调用收在适配器内，R1b）。</summary>
    public static IServiceCollection AddDiagnosticsCommands(this IServiceCollection services)
    {
        services.AddSingleton<ICommandRouter>(sp => new DesktopDiagnosticsCommandRouter(
            log: HostLog.Write,
            healthSnapshot: () => sp.GetRequiredService<PageHealthMonitor>().Snapshot));
        services.AddSingleton<IDiagnosticsExport>(sp => new DiagnosticsExportService(
            HostLog.Write,
            () => sp.GetRequiredService<PageHealthMonitor>().Snapshot));
        return services;
    }

    /// <summary>失败面（ADR native-failure-surface）：登记 Core 端口实现——宿主经端口承载失败面。不注册页面侧
    /// <c>dialog.*</c> 命令面、不放行能力位：宿主走插件静态调用不经能力闸，页面侧无调用者（最小授权）。</summary>
    public static IServiceCollection AddFailureSurface(this IServiceCollection services)
    {
        services.AddSingleton<IFailureDialog>(new RynFailureDialog(HostLog.Write));
        services.AddSingleton<IDialogOrphanReaper>(new DialogOrphanReaper(HostLog.Write));
        return services;
    }

    /// <summary>恢复页退出（desktop.recovery.exit）：先批准关窗闸门再 Close——hide-to-tray 拦截下
    /// 未批准的 Close 会吞成隐藏；顺序契约与托盘退出同款（ADR diag-masking-and-recovery-page）。
    /// 页内恢复面是原生对话框不可用时的回落承载面（ADR native-failure-surface），本命令随该回落面保留。
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
