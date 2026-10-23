using Avalonia.Controls;
using DeepSeek.Harness.Desktop.Core.Platform;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// 新宿主页面桥的装配与寿命（ADR architecture/2026-10-22-avalonia-g2-shell-bridge）：窗口出现后把
/// <see cref="NativeWebView"/> 交给 <see cref="NativeWebViewSurface"/>，建 <see cref="ShellBridge"/>、
/// 脚本求值端口与页面健康观测，并在窗口销毁时一并收口。
/// 代理未绑定（无壳 origin）时整体跳过：没有可信 origin 就没有可校验的桥，页面按静态降级页运行。
/// </summary>
internal sealed class ShellBridgeHost : IDisposable
{
    private readonly List<ICommandRouter> _routers;
    private readonly UiLocale _uiLocale;
    private volatile bool _captionBarEnabled;
    private volatile bool _edgeResize;
    private readonly string? _origin;
    private readonly ISystemBrowser _browser;
    private readonly bool _macChrome;
    private readonly int _captionHeightPx;
    private readonly TimeSpan _healthInterval;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private NativeWebViewSurface? _surface;
    private ShellBridge? _bridge;
    private PageHealthMonitor? _monitor;
    private bool _disposed;

    /// <summary>创建装配壳（尚未挂载窗口）。</summary>
    /// <param name="routers">命令路由表（PageBridge 域四条，G3 补齐其余；窗口命令在窗口就绪后追加）。</param>
    /// <param name="uiLocale">宿主 UI 语言单点（顶栏脚本的可达名回落）。</param>
    /// <param name="origin">壳 origin（代理 URL 的 authority）；null = 代理绑定失败。</param>
    /// <param name="browser">系统浏览器端口（外链拦截）。</param>
    /// <param name="macChrome">顶栏是否 macOS 形态。</param>
    /// <param name="captionHeightPx">顶栏高度（CSS 像素）。</param>
    /// <param name="healthInterval">健康探针首拍延迟/间隔。</param>
    /// <param name="log">日志委托。</param>
    public ShellBridgeHost(
        IReadOnlyList<ICommandRouter> routers,
        UiLocale uiLocale,
        string? origin,
        ISystemBrowser browser,
        bool macChrome,
        int captionHeightPx,
        TimeSpan healthInterval,
        Action<string> log)
    {
        _routers = [.. routers];
        _token = _cts.Token;
        _uiLocale = uiLocale;
        _origin = origin;
        _browser = browser;
        _macChrome = macChrome;
        _captionHeightPx = captionHeightPx;
        _healthInterval = healthInterval;
        _log = log;
    }

    /// <summary>命令路由快照（读侧加锁：写入发生在 UI 线程，分发在池线程）。</summary>
    /// <returns>当前路由数组。</returns>
    private IReadOnlyList<ICommandRouter> SnapshotRouters()
    {
        lock (_routers)
        {
            return _routers.ToArray();
        }
    }

    /// <summary>追加一条命令路由（窗口命令在窗口句柄就绪后由 <see cref="ShellWindow"/> 注册）。</summary>
    /// <param name="router">命令路由。</param>
    public void AddRouter(ICommandRouter router)
    {
        lock (_routers)
        {
            _routers.Add(router);
        }
    }

    /// <summary>能否承载自绘顶栏（桥已接上且壳 origin 存在）：无 origin 时 Attach 直接返回，
    /// 窗口必须保留原生装饰，否则会得到一条拖不动、也没有关闭按钮的窗。</summary>
    public bool CanHostCaptionBar => _surface is not null && !string.IsNullOrEmpty(_origin);

    /// <summary>最新页面健康快照（诊断包 state.txt 的收录源）：桥未接上时 null，与旧宿主
    /// <c>PageHealthMonitor.Snapshot</c> 同义。</summary>
    public string? HealthSnapshot => _monitor?.Snapshot;

    /// <summary>开关无边框顶栏注入（Wayland/无拖拽端口时关闭：窗口保留原生装饰）。</summary>
    /// <param name="enabled">true = 注入自绘顶栏。</param>
    /// <param name="edgeResize">宿主是否提供边缘缩放（macOS 由窗口系统代劳，页面不抢边）。</param>
    public void EnableCaptionBar(bool enabled, bool edgeResize)
    {
        _captionBarEnabled = enabled;
        _edgeResize = edgeResize;
        if (enabled)
        {
            // 导航期注入可能已错过（窗口在 Opened 才确定端口）：补一次，脚本自带幂等守卫。
            if (_surface is not null)
            {
                _ = PageCaptionBar.InjectAsync(
                    _surface, _captionHeightPx, _uiLocale, _macChrome, _edgeResize, _token, _log);
            }
        }
    }

    /// <summary>把已挂进视觉树的网页视图接上桥与健康观测（重复调用为 no-op）。</summary>
    /// <param name="webView">原生网页视图。</param>
    public void Attach(NativeWebView webView)
    {
        if (_disposed || _surface is not null)
        {
            return;
        }

        if (string.IsNullOrEmpty(_origin))
        {
            _log("[shell-bridge] 代理未绑定（无壳 origin）：跳过页面桥与健康观测");
            return;
        }

        _surface = new NativeWebViewSurface(webView);
        _bridge = new ShellBridge(
            _surface,
            SnapshotRouters,
            _origin,
            _browser,
            _macChrome,
            _captionHeightPx,
            _log,
            captionBarEnabled: () => _captionBarEnabled,
            uiLocale: _uiLocale,
            edgeResizeEnabled: () => _edgeResize);
        _bridge.Attach();

        NativeWebViewSurface surface = _surface;
        var monitor = new PageHealthMonitor(
            new AvaloniaWindowScriptTarget(surface),
            _log,
            reload: ct => new ValueTask(surface.ReloadAsync(ct)));
        _monitor = monitor;
        _ = monitor.RunAsync(_healthInterval, _cts.Token);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        try
        {
            _bridge?.Dispose();
        }
        finally
        {
            _surface?.Dispose();
            _cts.Dispose();
        }
    }
}
