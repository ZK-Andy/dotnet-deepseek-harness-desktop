using Ryn.Core;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 旧宿主（Ryn）的端口适配器（ADR architecture/2026-10-22-avalonia-g2-shell-bridge）：新端口
/// <see cref="IPageScriptRegistry"/> / <see cref="IPageEventPublisher"/> 在 Ryn 侧的等价实现，
/// 让 `DesktopAdapter` 的装配面继续按同一组端口接线，直到 G5 清理旧宿主。
/// </summary>
internal sealed class RynPageScriptRegistry : IPageScriptRegistry
{
    private readonly Func<IRynWebView> _webView;

    /// <summary>创建适配器。</summary>
    /// <param name="webView">网页视图惰性提供者（Build 后才有值）。</param>
    public RynPageScriptRegistry(Func<IRynWebView> webView) => _webView = webView;

    /// <inheritdoc />
    public Task RegisterOnLoadAsync(string script, CancellationToken ct) =>
        _webView().InjectScriptAsync(script, ct).AsTask();
}

/// <summary>Ryn 侧的事件推送适配器（<c>IRynWebView.EmitEvent</c>）。</summary>
internal sealed class RynPageEventPublisher : IPageEventPublisher
{
    private readonly Func<IRynWebView> _webView;

    /// <summary>创建适配器。</summary>
    /// <param name="webView">网页视图惰性提供者（Build 后才有值）。</param>
    public RynPageEventPublisher(Func<IRynWebView> webView) => _webView = webView;

    /// <inheritdoc />
    public void EmitEvent(string name, string payloadJson) => _webView().EmitEvent(name, payloadJson);
}
