using DeepSeek.Harness.Desktop.Core.Ui;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// <see cref="IWebViewNavigator"/> 的 Avalonia 适配器（ADR architecture/2026-10-23-avalonia-g1g3-recovery-bootstrap-supervision）：
/// 导航、到达信号与脚本求值三面都落在 <see cref="IWebViewSurface"/>（与旧宿主的
/// <see cref="WebViewNavigator"/> 同职）。到达信号取成功完成的顶层导航；求值回报按
/// <see cref="ScriptProbeValue.Decode"/> 解壳（桥回报是 JSON 字符串字面量）。
/// 不持有窗口类型，故本类可在 headless 测试里经假体跑通。
/// </summary>
internal sealed class AvaloniaWebViewNavigator : IWebViewNavigator, IDisposable
{
    private readonly IWebViewSurface _surface;
    private volatile Action _onArrived = static () => { };
    private int _disposed;

    /// <summary>创建适配器并订阅到达信号。</summary>
    /// <param name="surface">网页视图可测面。</param>
    public AvaloniaWebViewNavigator(IWebViewSurface surface)
    {
        _surface = surface;
        _surface.NavigationCompleted += OnNavigationCompleted;
    }

    /// <inheritdoc />
    public bool IsWindowReady => Volatile.Read(ref _disposed) == 0 && _surface.IsAvailable;

    /// <inheritdoc />
    public void SetArrivalCallback(Action onArrived) =>
        _onArrived = onArrived ?? throw new ArgumentNullException(nameof(onArrived));

    /// <inheritdoc />
    public Task NavigateAsync(Uri target, CancellationToken ct) => _surface.NavigateAsync(target, ct);

    /// <inheritdoc />
    public async Task<string?> EvaluateScriptAsync(string script) =>
        ScriptProbeValue.Decode(
            await _surface.InvokeScriptAsync(script, CancellationToken.None).ConfigureAwait(false));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _surface.NavigationCompleted -= OnNavigationCompleted;
    }

    /// <summary>到达信号只认成功完成的导航（失败导航由调用方的超时/探测判据处置）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">导航参数。</param>
    private void OnNavigationCompleted(object? sender, PageNavigationEventArgs e)
    {
        if (e.IsSuccess)
        {
            _onArrived();
        }
    }
}
