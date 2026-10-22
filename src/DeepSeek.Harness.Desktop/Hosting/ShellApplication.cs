using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>G1 应用对象：Fluent 主题、主窗、托盘与关窗到托盘。生命周期显式收口——关闭主窗只有在
/// 托盘不可用、平台注销/关机或托盘「退出」批准后才真正结束（<see cref="ShutdownMode.OnExplicitShutdown"/>），
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

    /// <summary>创建应用对象。</summary>
    /// <param name="startup">运行时起步（dsh spawn 与铸币）。</param>
    /// <param name="devTools">是否开启 WebView 调试器。</param>
    /// <param name="recall">唤回单点（二启通知与托盘点击共用）。</param>
    /// <param name="uiLocale">UI 语言单点（托盘菜单文案）。</param>
    /// <param name="trayIconPath">托盘图标路径（装配层探测，本类不碰文件系统）。</param>
    /// <param name="trayIconAvailable">托盘图标是否存在。</param>
    /// <param name="bridgeHost">页面桥装配（窗口挂载后接上，窗口销毁时收口）。</param>
    public ShellApplication(
        ShellStartup startup,
        bool devTools,
        ShellRecall recall,
        UiLocale uiLocale,
        string trayIconPath,
        bool trayIconAvailable,
        ShellBridgeHost bridgeHost)
    {
        _startup = startup;
        _devTools = devTools;
        _recall = recall;
        _uiLocale = uiLocale;
        _trayIconPath = trayIconPath;
        _trayIconAvailable = trayIconAvailable;
        _bridgeHost = bridgeHost;
    }

    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new ShellWindow(_startup.InitialUri, _devTools, _bridgeHost);
            desktop.MainWindow = window;

            var closeGate = new CloseGate();
            bool platformShutdown = false;
            var tray = ShellTray.TryCreate(
                _trayIconPath,
                _trayIconAvailable,
                _uiLocale,
                recall: _recall.Request,
                quit: () => Dispatcher.UIThread.Post(() =>
                {
                    closeGate.ApproveExit();
                    window.Close();
                }),
                log: HostLog.Write);

            window.Closing += (_, e) =>
            {
                bool platformInitiated = e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown;
                platformShutdown |= platformInitiated;
                if (!ShellClosePolicy.ShouldHide(closeGate.ExitApproved, tray is not null, platformInitiated))
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
                    tray?.Dispose();
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

        base.OnFrameworkInitializationCompleted();
    }
}
