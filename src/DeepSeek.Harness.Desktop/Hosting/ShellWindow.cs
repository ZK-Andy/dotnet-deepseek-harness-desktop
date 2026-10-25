using Avalonia.Controls;
using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>G1 主窗：单页承载包自带 <see cref="NativeWebView"/>，加载壳 origin（回环代理）。
/// 关窗拦截在 <see cref="ShellApplication"/>（托盘就绪且偏好允许才隐藏），本类只提供隐藏/唤回的窗口侧动作。
/// 导航拦截与页面桥由 <see cref="ShellBridgeHost"/> 在挂载后接管（ADR avalonia-g2-shell-bridge）；
/// 无边框顶栏与平台拖拽端口在 <c>OnOpened</c> 判定后启用（ADR 2026-10-23-avalonia-caption-bar）。</summary>
internal sealed class ShellWindow : Window
{
    private readonly ShellBridgeHost _bridgeHost;
    private readonly IWindowGeometryStore? _geometryStore;
    private int _chromeWired;
    private int _maximizedAtHide = -1;

    /// <summary>创建主窗并挂载 WebView。</summary>
    /// <param name="initialUri">初始 URL（壳 origin；null = 代理绑定失败，窗口留空。wwwroot 降级
    /// 与引导页仍留 G1 后续片）。</param>
    /// <param name="devTools">是否开启 WebView 调试器（<c>DSH_DEVTOOLS=1</c>，与 dev 判定无关）。</param>
    /// <param name="bridgeHost">页面桥装配（挂载 WebView 后接上；无壳 origin 时自身降级跳过）。</param>
    /// <param name="iconPath">窗口图标文件（装配层探测存在才传入；null = 不设，窗口用系统默认图标）。
    /// X11 上 Avalonia 据此写 <c>_NET_WM_ICON</c>——任务栏/Alt-Tab 图标的数据源（真机实测缺失）。</param>
    /// <param name="geometryStore">几何存储（null = 不持久化，用缺省尺寸）。</param>
    public ShellWindow(Uri? initialUri, bool devTools, ShellBridgeHost bridgeHost, string? iconPath = null, IWindowGeometryStore? geometryStore = null)
    {
        _bridgeHost = bridgeHost;
        _geometryStore = geometryStore;
        Title = "DeepSeek Harness";
        Width = 1200;
        Height = 800;
        if (iconPath is not null)
        {
            try
            {
                Icon = new WindowIcon(iconPath);
            }
            catch (Exception ex)
            {
                // 图标装载失败只影响任务栏/Alt-Tab 显示，不值得打断窗口构造
                HostLog.Write($"[host] 窗口图标装载失败（沿用系统默认）：{ex.Message}");
            }
        }

        if (geometryStore?.Load() is { } geometry)
        {
            WindowGeometryRestore.Apply(this, geometry);
        }

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

        // 几何落盘点（ADR 2026-10-25-window-identity-geometry-teardown）：窗口态变化即时存
        // （最大化/全屏切换，覆盖崩溃与非受控退出前的最后状态）；移动/缩放不在拖拽流里逐帧写盘，
        // 交给 HideToTray 与真实关窗两个收口点。自查 Closing 在外部判据之前订阅，两者互不影响。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                SaveGeometry();
            }
        };
        Closing += (_, _) => SaveGeometry();
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 窗口身份（WM_CLASS）先于一切分支：与打包面 StartupWMClass 配对，无壳 origin/无拖拽通道的
        // 降级窗口同样要有身份。幂等（属性替换写），托盘唤回重触发无害。
        X11WindowIdentity.Apply(this, HostLog.Write);
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

    /// <summary>隐藏到托盘：先记下隐藏前是否最大化，再落几何快照（dsh 运行时可能随后随机器一起消失，
    /// 下次冷启动以落盘值为准），最后隐藏。</summary>
    internal void HideToTray()
    {
        _maximizedAtHide = WindowState == WindowState.Maximized ? 1 : 0;
        SaveGeometry();
        Hide();
    }

    /// <summary>退出前置摘除页面内容（ADR 2026-10-25-window-identity-geometry-teardown）：真实关窗前
    /// 把 WebView 从视觉树摘下。真机实证收尾期 WebView 的 GTK 泵线程仍在绘制，直接拆窗会让在途绘制
    /// 撞上正在销毁的 EGL 上下文（WebKitGTK swapBuffers SEGV，coredump ×2 同栈）；先摘内容使泵线程
    /// 无 WebKit 可画，再走窗口销毁。幂等。</summary>
    internal void DetachContentForShutdown()
    {
        Content = null;
    }

    /// <summary>几何快照落盘（尽力而为）：存储缺失即 no-op。</summary>
    private void SaveGeometry()
    {
        if (_geometryStore is not { } store)
        {
            return;
        }

        store.Save(WindowGeometryRestore.Capture(this));
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
