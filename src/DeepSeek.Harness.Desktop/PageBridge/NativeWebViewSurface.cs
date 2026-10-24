using Avalonia.Controls;
using Avalonia.Threading;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// <see cref="IWebViewSurface"/> 的 Avalonia 适配器（ADR architecture/2026-10-22-avalonia-g2-shell-bridge）：
/// 把 <see cref="NativeWebView"/> 的四个事件翻译成桥接事件参数，并把脚本求值封送到 UI 线程
/// （Avalonia 控件只许 UI 线程访问；消息可能来自引擎线程）。取消/已处理标志回写给原生事件参数。
/// </summary>
internal sealed class NativeWebViewSurface : IWebViewSurface, IDisposable
{
    private readonly NativeWebView _webView;
    private volatile bool _disposed;

    /// <summary>创建适配器并订阅原生事件。</summary>
    /// <param name="webView">原生网页视图（已挂进视觉树）。</param>
    public NativeWebViewSurface(NativeWebView webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        _webView = webView;
        _webView.WebMessageReceived += OnWebMessageReceived;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
    }

    /// <inheritdoc />
    public event EventHandler<PageMessageEventArgs>? MessageReceived;

    /// <inheritdoc />
    public event EventHandler<PageNavigationEventArgs>? NavigationStarted;

    /// <inheritdoc />
    public event EventHandler<PageNavigationEventArgs>? NavigationCompleted;

    /// <inheritdoc />
    public event EventHandler<PageNavigationEventArgs>? NewWindowRequested;

    /// <inheritdoc />
    public bool IsAvailable => !_disposed;

    /// <inheritdoc />
    public async Task<string?> InvokeScriptAsync(string script, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Task<string?> marshalled = Dispatcher.UIThread.InvokeAsync(() => _webView.InvokeScript(script));
            return await marshalled.WaitAsync(ct).ConfigureAwait(false);
        }

        return await _webView.InvokeScript(script).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task NavigateAsync(Uri target, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Task navigation = Dispatcher.UIThread
                .InvokeAsync(() => _webView.Source = target)
                .GetTask();
            await navigation.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        _webView.Source = target;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _webView.WebMessageReceived -= OnWebMessageReceived;
        _webView.NavigationStarted -= OnNavigationStarted;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.NewWindowRequested -= OnNewWindowRequested;
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e) =>
        MessageReceived?.Invoke(this, new PageMessageEventArgs(e.Body ?? string.Empty));

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        var args = new PageNavigationEventArgs(e.Request);
        NavigationStarted?.Invoke(this, args);
        if (args.Cancel)
        {
            e.Cancel = true;
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e) =>
        NavigationCompleted?.Invoke(this, new PageNavigationEventArgs(e.Request, e.IsSuccess));

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        var args = new PageNavigationEventArgs(e.Request);
        NewWindowRequested?.Invoke(this, args);
        if (args.Handled)
        {
            e.Handled = true;
        }
    }
}
