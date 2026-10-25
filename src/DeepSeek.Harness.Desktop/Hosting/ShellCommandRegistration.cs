using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// G3 命令面注册：开机自启、关闭到托盘、诊断导出与应用重启四条路由，以及它们共用的关窗偏好、
/// 导出器两个装配期单例与托盘退出/重启动作（ADR implemented/architecture/2026-10-23-avalonia-g3-shell-commands、
/// -g3-exit-restart）。
/// 基础设施具体类型的构造收在本文件（R1a 注册纪律：注册文件之外不 new 基础设施类型）。
/// 页面桥域四条路由仍在 <see cref="PageBridgeRegistration.CreateShellBridgeHost"/>；窗口命令由
/// <see cref="ShellWindow"/> 在顶栏启用后追加；更新/首启/恢复三条随各自片接入（见 ADR 的范围段）。
/// </summary>
internal static class ShellCommandRegistration
{
    /// <summary>关窗偏好（「关闭时最小化到托盘」）：<c>&lt;DSH_HOME&gt;/desktop-preferences.json</c>，
    /// 缺文件回退默认 true（与历史行为一致）。</summary>
    /// <param name="dshHome">DSH home 绝对路径。</param>
    /// <returns>偏好端口（文件落盘留在实现侧）。</returns>
    public static ICloseBehaviorPreference CreateCloseBehavior(string dshHome) =>
        new CloseBehaviorPreference(Path.Combine(dshHome, CloseBehaviorPreference.FileName));

    /// <summary>主窗几何持久化（ADR 2026-10-25-window-identity-geometry-teardown）：desktop profile 状态
    /// 目录下的 window-state.json。</summary>
    /// <param name="log">日志回调。</param>
    /// <returns>几何存储端口（文件落盘留在实现侧）。</returns>
    public static IWindowGeometryStore CreateWindowGeometryStore(Action<string> log) =>
        new WindowGeometryStore(WindowGeometryStore.DefaultFilePath(), log);

    /// <summary>诊断包导出器：当前唯一消费方是 <c>desktop.diagnostics.export</c>；失败面自动导出
    /// 尚未接（记在 ADR 的残留面）。</summary>
    /// <param name="log">日志回调。</param>
    /// <returns>导出端口实现。</returns>
    public static IDiagnosticsExport CreateDiagnosticsExport(Action<string> log) =>
        new DiagnosticsExportService(log);

    /// <summary>本批四条路由：开机自启（companion 桌面设置区）、关闭到托盘开关（状态帧携带
    /// <c>available</c>）、诊断导出（设置页按钮）、应用重启（companion 重启行）。</summary>
    /// <param name="closeBehavior">关窗偏好端口（路由读写；关窗判据读同一实例）。</param>
    /// <param name="trayReady">托盘是否就绪（决定 closeToTray 状态帧的 <c>available</c>；调用期求值）。</param>
    /// <param name="diagnostics">诊断导出端口。</param>
    /// <param name="closeGate">关窗闸门（重启路由先批准再回收关窗）。</param>
    /// <param name="restart">重启动作（<see cref="CreateTrayActions"/> 的产物：托盘菜单与命令入口共用
    /// 同一个动作，避免两条 spawn 路径各持一份默认值）。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>路由表。</returns>
    public static IReadOnlyList<ICommandRouter> CreateRouters(
        ICloseBehaviorPreference closeBehavior,
        Func<bool> trayReady,
        IDiagnosticsExport diagnostics,
        CloseGate closeGate,
        Action restart,
        Action<string> log) =>
    [
        new AutostartCommandRouter(new AutostartService(), log),
        new CloseToTrayCommandRouter(closeBehavior, trayReady, log),
        new DesktopDiagnosticsCommandRouter(diagnostics, log),
        new AppRestartCommandRouter(closeGate, restart, log),
    ];

    /// <summary>托盘菜单的退出/重启动作（ADR -g3-exit-restart）：两者都先批准关窗闸门再走管道——
    /// 未批准的关窗会被 hide-to-tray 吞成隐藏，菜单「退出」就成了「藏起来」。
    /// 回收跑在线程池：原生菜单回调在 UI 线程，`HarnessRuntimeHost.Stop` 的门等待与进程回收
    /// 不能占着 UI 线程；管道自己把关窗动作投递回 UI 线程。</summary>
    /// <param name="closeGate">关窗闸门。</param>
    /// <param name="exitPipeline">退出管道。</param>
    /// <param name="spawnSuccessor">拉起新实例的动作；null = 生产路径（<see cref="AppRelaunch.SpawnSelf"/>），
    /// 测试注入记序 fake（与 <see cref="ExitPipeline"/> 的看门狗注入口径一致）。</param>
    /// <param name="log">日志回调（spawn 失败留证用；null = 不留痕）。</param>
    /// <returns>退出与重启动作对（原生菜单回调直接调用）。</returns>
    public static (Action Quit, Action Restart) CreateTrayActions(
        CloseGate closeGate,
        ExitPipeline exitPipeline,
        Action? spawnSuccessor = null,
        Action<string>? log = null)
    {
        Action spawn = spawnSuccessor ?? AppRelaunch.SpawnSelf;
        return (
            () =>
            {
                closeGate.ApproveExit();
                _ = Task.Run(() => RunGuarded(exitPipeline.OrderlyQuit, log));
            },
            () =>
            {
                closeGate.ApproveExit();
                _ = Task.Run(() => RunGuarded(
                    () => exitPipeline.Restart(() => SpawnGuarded(spawn, log)), log));
            }
        );
    }

    /// <summary>管道体守卫：回收/释放/关窗的异常在后台任务里无人观察；留一行日志即处置。
    /// 闸门已批准，异常后窗口关闭按钮仍能退出（后台不再有后续动作）。</summary>
    /// <param name="run">管道动作。</param>
    /// <param name="log">日志回调。</param>
    private static void RunGuarded(Action run, Action<string>? log)
    {
        try
        {
            run();
        }
        catch (Exception ex)
        {
            log?.Invoke($"[host] 退出/重启管道失败：{ex.Message}");
        }
    }

    /// <summary>拉起新实例的守卫：spawn 抛错时管道已经回收（停宿主/释放 marker/监听器，
    /// 且 once-guard 已置位），此时绝不能让异常吞掉关窗——否则留下 dsh 已死、窗口还在、
    /// 看门狗未武装的僵尸壳。留证后返回，管道继续关窗，本次按退出处理。</summary>
    /// <param name="spawn">拉起新实例的动作。</param>
    /// <param name="log">日志回调。</param>
    private static void SpawnGuarded(Action spawn, Action<string>? log)
    {
        try
        {
            spawn();
        }
        catch (Exception ex)
        {
            log?.Invoke($"[host] 重启拉起新实例失败：{ex.Message}；本次按退出处理");
        }
    }
}
