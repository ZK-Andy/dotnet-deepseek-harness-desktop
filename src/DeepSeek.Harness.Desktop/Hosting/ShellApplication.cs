using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Core.Platform;

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
    private readonly IWindowGeometryStore? _geometryStore;
    private readonly ExitPipeline _exitPipeline;
    private readonly ShellRecoveryContext _recoveryContext;
    private readonly CloseGate _closeGate;
    private readonly UpdateSurface _updates;
    private readonly Action _closeWindow;
    private readonly StartupNoticeService _notices;
    private ShellRecoveryAssembly? _recovery;
    private bool _platformShutdown;
    private int _startupHooks;
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
        _startup.AttachFailureSink(exception => _ = Task.Run(() =>
        {
            HostLog.Write($"[host] 启动失败面接管：{exception.GetType().Name}");
            _ = face.ShowAsync(CancellationToken.None);
        }));
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
    /// <param name="geometryStore">主窗几何存储（冷启动还原与退出/隐藏落盘；null = 不持久化，用缺省尺寸）。</param>
    /// <param name="exitPipeline">退出管道（托盘退出/重启与 desktop.app.restart 共用；回收序在管道内，
    /// ADR avalonia-g3-exit-restart）。</param>
    /// <param name="recoveryContext">恢复/引导/监督的宿主侧依赖束（ADR avalonia-g1g3-recovery-bootstrap-supervision）。</param>
    /// <param name="closeGate">关窗闸门（失败面/托盘/恢复页/自更新安装共用的同一放行点）。</param>
    /// <param name="updates">自更新面（ADR avalonia-g3-update-surface：命令路由、启动检查与托盘检查动作）。</param>
    /// <param name="closeWindow">主窗关窗投递（退出管道、自更新安装与恢复页退出共用同一份）。</param>
    /// <param name="notices">启动期告知（版本底线/非受控退出横幅；起步收口后触发）。</param>
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
        IWindowGeometryStore? geometryStore,
        ExitPipeline exitPipeline,
        ShellRecoveryContext recoveryContext,
        CloseGate closeGate,
        UpdateSurface updates,
        Action closeWindow,
        StartupNoticeService notices)
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
        _geometryStore = geometryStore;
        _exitPipeline = exitPipeline;
        _recoveryContext = recoveryContext;
        _closeGate = closeGate;
        _updates = updates;
        _closeWindow = closeWindow;
        _notices = notices;
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
    private void StartShell(IClassicDesktopStyleApplicationLifetime desktop) => WireShellSurface(desktop);

    /// <summary>窗口/托盘/关窗/唤回接线（拆自 <see cref="StartShell"/> 以守 F2 方法长度）：
    /// 闸门先于窗口，托盘就绪决定关窗是否可隐藏，关闭路径统一收口桥与托盘。</summary>
    /// <param name="desktop">桌面生命周期。</param>
    private void WireShellSurface(IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // 闸门在接线层构造（先于窗口）：启动失败面的「退出」、托盘「退出」与自更新安装共用同一放行点。
        CloseGate closeGate = _closeGate;
        var window = new ShellWindow(_startup.InitialUri, _devTools, _bridgeHost, _trayIconAvailable ? _trayIconPath : null, _geometryStore);
        desktop.MainWindow = window;

        // 退出/重启动作先于失败面与托盘构造（ADR avalonia-g3-exit-restart）：失败面「退出」、托盘
        // 「退出/重启」与 desktop.app.restart 三处消费同一对动作，回收序只在退出管道里。
        (Action quit, Action restart) = ShellCommandRegistration.CreateTrayActions(
            closeGate, _exitPipeline, log: HostLog.Write);

        WireTray(quit, restart);
        RegisterCommandRouters(closeGate, restart);
        WireRecovery(closeGate, window);
        WireWindowLifecycle(desktop, window, closeGate);

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

        window.Opened += (_, _) =>
        {
            // 起步早于建窗：显示后补挂失败面与监督（只交付一次）；Show() 唤回会重触发 Opened，接线只做一次。
            if (Interlocked.Exchange(ref _startupHooks, 1) == 1)
            {
                return;
            }

            WireStartupFailure(window, quit);
            _startup.AttachAfterStart(StartRecoveryAfterStartup);
            _updates.Start();
        };
    }

    /// <summary>托盘创建（无图标/无托盘环境回落 null，关窗判据据此直退）。</summary>
    /// <param name="quit">退出动作。</param>
    /// <param name="restart">重启动作。</param>
    private void WireTray(Action quit, Action restart) =>
        _tray = ShellTray.TryCreate(
            _trayIconPath,
            _trayIconAvailable,
            _uiLocale,
            recall: _recall.Request,
            checkUpdate: _updates.CheckUpdate,
            restart: restart,
            quit: quit,
            log: HostLog.Write);

    /// <summary>命令面（ADR avalonia-g3-shell-commands）：托盘就绪状态决定 closeToTray 状态帧的
    /// available；路由表在窗口挂载后加入即可（桥每次分发都重读路由快照）。</summary>
    /// <param name="closeGate">关窗闸门（重启路由先批准再回收关窗）。</param>
    /// <param name="restart">重启动作。</param>
    private void RegisterCommandRouters(CloseGate closeGate, Action restart)
    {
        foreach (ICommandRouter router in ShellCommandRegistration.CreateRouters(
                     _closeBehavior, () => _tray is not null, _diagnostics, closeGate, restart, HostLog.Write))
        {
            _bridgeHost.AddRouter(router);
        }

        // 自更新三条路由（ADR avalonia-g3-update-surface）：dev 门禁下为空表（invoke 自然失败，与旧宿主同形）。
        foreach (ICommandRouter router in _updates.Routers)
        {
            _bridgeHost.AddRouter(router);
        }
    }

    /// <summary>恢复/引导/监督（ADR avalonia-g1g3-recovery-bootstrap-supervision）：网页视图已随窗口构造挂上桥。
    /// 无壳 origin 时 Surface 为 null——页面面无承载面，整组跳过；监督器在没有失败面可展示时启动
    /// 只会空转覆写，故不启（启动失败面已由 <see cref="StartupFailureFace"/> 承担）。</summary>
    /// <param name="closeGate">关窗闸门（恢复页退出与托盘退出同一条批准契约）。</param>
    /// <param name="window">属主窗口（模态归属与关窗动作）。</param>
    private void WireRecovery(CloseGate closeGate, Window window)
    {
        if (_bridgeHost.Surface is not { } surface)
        {
            HostLog.Write("[host] 无壳 origin：跳过恢复页/引导/监督接线（窗口无页面；启动失败面仍可用）");
            return;
        }

        Action closeWindow = _closeWindow;
        _recovery = ShellRecoveryRegistration.Create(
            _recoveryContext.Host,
            surface,
            _bridgeHost.ScriptTarget,
            _startup.InitialUri,
            _recoveryContext.Bootstrap,
            _diagnostics,
            _uiLocale,
            closeGate,
            () => window,
            closeWindow,
            _recoveryContext.Timeouts,
            _recoveryContext.ShellForward,
            HostLog.Write);
        foreach (ICommandRouter router in _recovery.Routers)
        {
            _bridgeHost.AddRouter(router);
        }
    }

    /// <summary>关窗与收尾：隐藏到托盘或放行；真正关闭时先取消引导/监督再释放桥与托盘。</summary>
    /// <param name="desktop">桌面生命周期。</param>
    /// <param name="window">主窗。</param>
    /// <param name="closeGate">关窗闸门。</param>
    private void WireWindowLifecycle(
        IClassicDesktopStyleApplicationLifetime desktop,
        ShellWindow window,
        CloseGate closeGate)
    {
        window.Closing += (_, e) =>
        {
            bool platformInitiated = e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown;
            _platformShutdown |= platformInitiated;
            if (!ShellClosePolicy.ShouldHide(closeGate.ExitApproved, _tray is not null, _closeBehavior.HideOnClose, platformInitiated))
            {
                // 真实关窗：先摘 WebView 再让 Close 展开（ADR 2026-10-25-window-identity-geometry-teardown）——
                // 收尾期 GTK 泵线程的在途绘制会撞上正在销毁的 EGL 上下文（真机 coredump ×2 同栈）。
                window.DetachContentForShutdown();
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
                // 引导/监督先于桥收口：监督循环可能在等页面求值，桥必须先取消再释放（顺序与退出管道一致）。
                _recoveryContext.Cancel();
                _bridgeHost.Dispose();
                _tray?.Dispose();
            }
            finally
            {
                // 平台发起的关闭已在自己的收尾流程里，重复 Shutdown 会重入 DoShutdown
                if (!_platformShutdown)
                {
                    desktop.Shutdown();
                }
            }
        };
    }

    /// <summary>起步收口后接引导与监督（ADR avalonia-g1g3-recovery-bootstrap-supervision）：引导服务
    /// 只在首启需要时真正启动（否则 no-op），监督循环等引导落定后进入恢复循环。两者都在池线程上跑，
    /// 页面面经桥端口与 UI 线程封送；起步若在窗口显示前已收口，本方法在 `Opened` 的 UI 线程上同步执行。</summary>
    private void StartRecoveryAfterStartup()
    {
        ShellRecoveryAssembly? recovery = _recovery;
        CancellationToken ct = _recoveryContext.SupervisorCts.Token;
        // 关窗与起步收口的竞态：取消已发生则不再拉起引导（Start 会新建无人取消的 CTS 并 spawn）。
        if (recovery is null || ct.IsCancellationRequested)
        {
            return;
        }

        _recoveryContext.Bootstrap.Start((url, entryCt) => recovery.Entry.RunAsync(url, entryCt));
        // 复检之后的竞态窗口：关闭若落在 Start 内，上面新建的引导 CTS 无人取消——就地补取消。
        if (ct.IsCancellationRequested)
        {
            _recoveryContext.Bootstrap.Cancel();
            return;
        }

        // 启动期告知（版本底线/非受控退出横幅）：与监督并行；取消随监督器，异常留痕（增强面不炸启动）。
        _ = _notices.RunAsync(ct).ContinueWith(
            t => HostLog.Write($"[host] 启动期告知异常退出：{t.Exception}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Task supervision = recovery.Supervision.RunAsync(_recoveryContext.Host, ct);
        _recoveryContext.SupervisionTask = supervision;
        _ = supervision.ContinueWith(
            t => HostLog.Write($"[host] 监督循环异常退出：{t.Exception}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
