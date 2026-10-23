using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DeepSeek.Harness.Desktop.Core.Platform;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>G1 应用对象：Fluent 主题、主窗、托盘与关窗到托盘。生命周期显式收口——关闭主窗只有在
/// 托盘不可用、`desktop.closeToTray` 偏好为关、平台注销/关机或托盘「退出」批准后才真正结束（<see cref="ShutdownMode.OnExplicitShutdown"/>），
/// 否则隐藏到托盘继续跑 dsh。关窗判据住 <see cref="ShellClosePolicy"/>（可单测）。</summary>
internal sealed class ShellApplication : Application
{
    private readonly ShellStartup _startup;
    private readonly bool _devTools;
    private readonly ShellRecall _recall;
    private readonly UiLocale _uiLocale;
    private readonly string _trayIconPath;
    private readonly bool _trayIconAvailable;
    private readonly ShellBridgeHost _bridgeHost;
    private readonly Func<IReadOnlyList<string>> _stderrTail;
    private readonly ICloseBehaviorPreference _closeBehavior;
    private readonly IDiagnosticsExport _diagnostics;
    private readonly ExitPipeline _exitPipeline;
    private volatile ShellTray? _tray;

    /// <summary>启动失败面接线（ADR 2026-10-23-avalonia-failure-dialog）：起步体在后台线程失败，
    /// 模态经端口自行封送 UI 线程；退出动作与托盘共用同一对动作里的 quit（先批准闸门再走管道）。</summary>
    /// <param name="window">属主窗口（模态的归属）。</param>
    /// <param name="quit">退出动作（托盘/失败面/命令面同源）。</param>
    private void WireStartupFailure(Window window, Action quit)
    {
        var face = new StartupFailureFace(
            new InProcessFailureDialog(() => window, _uiLocale, HostLog.Write),
            _stderrTail,
            _uiLocale,
            quit: quit,
            log: HostLog.Write);
        _startup.Failed += exception => _ = Task.Run(() =>
        {
            HostLog.Write($"[host] 启动失败面接管：{exception.GetType().Name}");
            _ = face.ShowAsync(CancellationToken.None);
        });
    }

    /// <summary>创建应用对象。</summary>
    /// <param name="startup">运行时起步（dsh spawn 与铸币）。</param>
    /// <param name="devTools">是否开启 WebView 调试器。</param>
    /// <param name="recall">唤回单点（二启通知与托盘点击共用）。</param>
    /// <param name="uiLocale">UI 语言单点（托盘菜单文案）。</param>
    /// <param name="trayIconPath">托盘图标路径（装配层探测，本类不碰文件系统）。</param>
    /// <param name="trayIconAvailable">托盘图标是否存在。</param>
    /// <param name="bridgeHost">页面桥装配（窗口挂载后接上，窗口销毁时收口）。</param>
    /// <param name="stderrTail">子进程 stderr 尾部取法（启动失败面正文的数据源）。</param>
    /// <param name="closeBehavior">关窗偏好：关窗判据与 desktop.closeToTray 路由共用同一实例
    /// （ADR avalonia-g3-shell-commands）。</param>
    /// <param name="diagnostics">诊断导出端口（desktop.diagnostics.export）。</param>
    /// <param name="exitPipeline">退出管道（托盘退出/重启与 desktop.app.restart 共用；回收序在管道内，
    /// ADR avalonia-g3-exit-restart）。</param>
    public ShellApplication(
        ShellStartup startup,
        bool devTools,
        ShellRecall recall,
        UiLocale uiLocale,
        string trayIconPath,
        bool trayIconAvailable,
        ShellBridgeHost bridgeHost,
        Func<IReadOnlyList<string>> stderrTail,
        ICloseBehaviorPreference closeBehavior,
        IDiagnosticsExport diagnostics,
        ExitPipeline exitPipeline)
    {
        _startup = startup;
        _devTools = devTools;
        _recall = recall;
        _uiLocale = uiLocale;
        _trayIconPath = trayIconPath;
        _trayIconAvailable = trayIconAvailable;
        _bridgeHost = bridgeHost;
        _stderrTail = stderrTail;
        _closeBehavior = closeBehavior;
        _diagnostics = diagnostics;
        _exitPipeline = exitPipeline;
    }

    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartShell(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>窗口/托盘/关窗/唤回接线（ADR avalonia-g1-tray-recall、-g3-shell-commands）：闸门先于窗口，
    /// 托盘就绪决定关窗是否可隐藏，关闭路径统一收口桥与托盘，最后按平台是否发起决定是否结束生命周期。</summary>
    /// <param name="desktop">桌面生命周期。</param>
    private void StartShell(IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // 闸门先于窗口：启动失败面的「退出」与托盘「退出」共用同一放行点。
        var closeGate = new CloseGate();
        var window = new ShellWindow(_startup.InitialUri, _devTools, _bridgeHost);
        desktop.MainWindow = window;

        // 退出/重启动作先于失败面与托盘构造（ADR avalonia-g3-exit-restart）：失败面「退出」、托盘
        // 「退出/重启」与 desktop.app.restart 三处消费同一对动作，回收序只在退出管道里。
        (Action quit, Action restart) = ShellCommandRegistration.CreateTrayActions(
            closeGate, _exitPipeline, log: HostLog.Write);

        WireStartupFailure(window, quit);
        bool platformShutdown = false;
        _tray = ShellTray.TryCreate(
            _trayIconPath,
            _trayIconAvailable,
            _uiLocale,
            recall: _recall.Request,
            restart: restart,
            quit: quit,
            log: HostLog.Write);

        // 命令面（ADR avalonia-g3-shell-commands）：托盘就绪状态决定 closeToTray 状态帧的
        // available；路由表在窗口挂载后加入即可（桥每次分发都重读路由快照）。
        foreach (ICommandRouter router in ShellCommandRegistration.CreateRouters(
                     _closeBehavior, () => _tray is not null, _diagnostics, closeGate, restart, HostLog.Write))
        {
            _bridgeHost.AddRouter(router);
        }

        window.Closing += (_, e) =>
        {
            bool platformInitiated = e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown;
            platformShutdown |= platformInitiated;
            if (!ShellClosePolicy.ShouldHide(closeGate.ExitApproved, _tray is not null, _closeBehavior.HideOnClose, platformInitiated))
            {
                return;
            }

            e.Cancel = true;
            window.HideToTray();
            HostLog.Write("[host] 关窗：隐藏到托盘（dsh 运行时继续；再启动一次即可唤回）");
        };

        window.Closed += (_, _) =>
        {
            _recall.Detach();
            try
            {
                _bridgeHost.Dispose();
                _tray?.Dispose();
            }
            finally
            {
                // 平台发起的关闭已在自己的收尾流程里，重复 Shutdown 会重入 DoShutdown
                if (!platformShutdown)
                {
                    desktop.Shutdown();
                }
            }
        };

        _recall.Attach(() => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                window.RecallFromTray();
            }
            catch (InvalidOperationException)
            {
                // 窗口已关闭：Avalonia 拒绝 re-show，Detach 前的竞态窗口里丢弃这次迟到唤回
            }
        }));

        window.Opened += (_, _) => _startup.Begin();
    }
}
