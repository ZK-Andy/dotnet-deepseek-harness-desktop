using DeepSeek.Harness.Desktop.Core.Platform;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// G3 首批命令面注册：开机自启、关闭到托盘、诊断导出三条路由，以及它们共用的关窗偏好与
/// 导出器两个装配期单例（ADR implemented/architecture/2026-10-23-avalonia-g3-shell-commands）。
/// 基础设施具体类型的构造收在本文件（R1a 注册纪律：注册文件之外不 new 基础设施类型）。
/// 页面桥域四条路由仍在 <see cref="PageBridgeRegistration.CreateShellBridgeHost"/>；窗口命令由
/// <see cref="ShellWindow"/> 在顶栏启用后追加；其余五条随各自片接入（见 ADR 的范围段）。
/// </summary>
internal static class ShellCommandRegistration
{
    /// <summary>关窗偏好（「关闭时最小化到托盘」）：<c>&lt;DSH_HOME&gt;/desktop-preferences.json</c>，
    /// 缺文件回退默认 true（与历史行为一致）。</summary>
    /// <param name="dshHome">DSH home 绝对路径。</param>
    /// <returns>偏好端口（文件落盘留在实现侧）。</returns>
    public static ICloseBehaviorPreference CreateCloseBehavior(string dshHome) =>
        new CloseBehaviorPreference(Path.Combine(dshHome, CloseBehaviorPreference.FileName));

    /// <summary>诊断包导出器：当前唯一消费方是 <c>desktop.diagnostics.export</c>；失败面自动导出
    /// 尚未接（记在 ADR 的残留面）。健康快照在调用期求值，观测未起时为 null。</summary>
    /// <param name="healthSnapshot">页面健康快照取法（<see cref="ShellBridgeHost.HealthSnapshot"/>）。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>导出端口实现。</returns>
    public static IDiagnosticsExport CreateDiagnosticsExport(Func<string?> healthSnapshot, Action<string> log) =>
        new DiagnosticsExportService(log, healthSnapshot);

    /// <summary>本批三条路由：开机自启（companion 桌面设置区）、关闭到托盘开关（状态帧携带
    /// <c>available</c>）、诊断导出（设置页按钮）。</summary>
    /// <param name="closeBehavior">关窗偏好端口（路由读写；关窗判据读同一实例）。</param>
    /// <param name="trayReady">托盘是否就绪（决定 closeToTray 状态帧的 <c>available</c>；调用期求值）。</param>
    /// <param name="diagnostics">诊断导出端口。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>路由表。</returns>
    public static IReadOnlyList<ICommandRouter> CreateRouters(
        ICloseBehaviorPreference closeBehavior,
        Func<bool> trayReady,
        IDiagnosticsExport diagnostics,
        Action<string> log) =>
    [
        new AutostartCommandRouter(new AutostartService(), log),
        new CloseToTrayCommandRouter(closeBehavior, trayReady, log),
        new DesktopDiagnosticsCommandRouter(diagnostics, log),
    ];
}
