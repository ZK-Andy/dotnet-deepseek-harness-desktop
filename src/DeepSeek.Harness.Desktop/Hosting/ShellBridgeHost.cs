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
    private readonly IReadOnlyList<ICommandRouter> _routers;
    private readonly string? _origin;
    private readonly ISystemBrowser _browser;
    private readonly bool _macChrome;
    private readonly int _captionHeightPx;
    private readonly TimeSpan _healthInterval;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private NativeWebViewSurface? _surface;
    private ShellBridge? _bridge;
    private bool _disposed;

    /// <summary>创建装配壳（尚未挂载窗口）。</summary>
    /// <param name="routers">命令路由表（PageBridge 域四条，G3 补齐其余）。</param>
    /// <param name="origin">壳 origin（代理 URL 的 authority）；null = 代理绑定失败。</param>
    /// <param name="browser">系统浏览器端口（外链拦截）。</param>
    /// <param name="macChrome">顶栏是否 macOS 形态。</param>
    /// <param name="captionHeightPx">顶栏高度（CSS 像素）。</param>
    /// <param name="healthInterval">健康探针首拍延迟/间隔。</param>
    /// <param name="log">日志委托。</param>
    public ShellBridgeHost(
        IReadOnlyList<ICommandRouter> routers,
        string? origin,
        ISystemBrowser browser,
        bool macChrome,
        int captionHeightPx,
        TimeSpan healthInterval,
        Action<string> log)
    {
        _routers = routers;
        _origin = origin;
        _browser = browser;
        _macChrome = macChrome;
        _captionHeightPx = captionHeightPx;
        _healthInterval = healthInterval;
        _log = log;
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
            _surface, _routers, _origin, _browser, _macChrome, _captionHeightPx, _log);
        _bridge.Attach();

        NativeWebViewSurface surface = _surface;
        var monitor = new PageHealthMonitor(
            new AvaloniaWindowScriptTarget(surface),
            _log,
            reload: ct => new ValueTask(surface.ReloadAsync(ct)));
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
