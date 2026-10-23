using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>G1 主窗：单页承载包自带 <see cref="NativeWebView"/>，加载壳 origin（回环代理）。
/// 关窗拦截在 <see cref="ShellApplication"/>（托盘就绪且偏好允许才隐藏），本类只提供隐藏/唤回的窗口侧动作。
/// 导航拦截与页面桥由 <see cref="ShellBridgeHost"/> 在挂载后接管（ADR avalonia-g2-shell-bridge）；
/// 无边框顶栏与平台拖拽端口在 <c>OnOpened</c> 判定后启用（ADR 2026-10-23-avalonia-caption-bar）。</summary>
internal sealed class ShellWindow : Window
{
    private readonly ShellBridgeHost _bridgeHost;
    private int _chromeWired;
    private int _maximizedAtHide = -1;

    /// <summary>创建主窗并挂载 WebView。</summary>
    /// <param name="initialUri">初始 URL（壳 origin；null = 代理绑定失败，窗口留空。wwwroot 降级
    /// 与引导页仍留 G1 后续片）。</param>
    /// <param name="devTools">是否开启 WebView 调试器（<c>DSH_DEVTOOLS=1</c>，与 dev 判定无关）。</param>
    /// <param name="bridgeHost">页面桥装配（挂载 WebView 后接上；无壳 origin 时自身降级跳过）。</param>
    public ShellWindow(Uri? initialUri, bool devTools, ShellBridgeHost bridgeHost)
    {
        _bridgeHost = bridgeHost;
        Title = "DeepSeek Harness";
        Width = 1200;
        Height = 800;

        var webView = new NativeWebView();
        webView.EnvironmentRequested += (_, e) => e.EnableDevTools = devTools;
        bridgeHost.Attach(webView);
        if (initialUri is not null)
        {
            webView.Source = initialUri;
        }

        // Windows/macOS 的 BorderOnly 保留原生边框与缩放、去掉标题栏；Linux 先留原生装饰，
        // 句柄就绪后由 OnOpened 判端口：有 X11 通道才切无边框（Wayland 无通道，保留装饰）。
        WindowDecorations = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? WindowDecorations.BorderOnly
            : WindowDecorations.Full;

        Content = webView;
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Show() 也会触发 OnOpened（托盘唤回）：接线只做一次，否则每次唤回都追加一条路由。
        if (Interlocked.Exchange(ref _chromeWired, 1) == 1)
        {
            return;
        }

        if (!_bridgeHost.CanHostCaptionBar)
        {
            // 代理未绑定（无壳 origin）：桥与顶栏注入整体不成立，保留原生装饰。
            HostLog.Write("[host] 无壳 origin：跳过无边框顶栏，保留原生装饰");
            return;
        }

        IWindowDrag drag = WindowDrag.For(this, HostLog.Write);
        if (drag is UnsupportedWindowDrag)
        {
            // 无拖拽通道（Wayland/句柄缺失）：不自绘顶栏，窗口保持原生装饰——不给出拖不动的窗。
            WindowDecorations = WindowDecorations.Full;
            _bridgeHost.EnableCaptionBar(enabled: false, edgeResize: false);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            WindowDecorations = WindowDecorations.None;
        }

        _bridgeHost.AddRouter(
            new WindowCommandRouter(new AvaloniaWindowCommands(this, drag, HostLog.Write), HostLog.Write));
        // macOS 的边框缩放由窗口系统按 BorderOnly 提供，页面不得再抢 6px 边缘。
        _bridgeHost.EnableCaptionBar(enabled: true, edgeResize: !string.Equals(drag.Name, "macos", StringComparison.Ordinal));
        HostLog.Write($"[host] 无边框顶栏启用：拖拽/缩放端口 {drag.Name}");
    }

    /// <summary>隐藏到托盘：先记下隐藏前是否最大化，再隐藏（进程与 dsh 运行时继续存活）。</summary>
    internal void HideToTray()
    {
        _maximizedAtHide = WindowState == WindowState.Maximized ? 1 : 0;
        Hide();
    }

    /// <summary>从托盘/二启唤回：还原最小化、显示并置前；隐藏前是最大化则恢复最大化，
    /// 随后消费样本（残留会让下一次唤回误最大化）。</summary>
    internal void RecallFromTray()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        if (TrayRecallMaximize.ShouldEnsure(_maximizedAtHide))
        {
            WindowState = WindowState.Maximized;
        }

        _maximizedAtHide = -1;
    }
}
