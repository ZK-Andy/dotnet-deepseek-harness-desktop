using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>G0 主窗：单页承载包自带 <see cref="NativeWebView"/>，加载壳 origin（回环代理）。
/// 无边框/顶栏/托盘唤回留 G1（ADR proposed/architecture/2026-10-22-avalonia-host-migration）。</summary>
internal sealed class ShellWindow : Window
{
    private readonly string _origin;

    /// <summary>创建主窗并挂载 WebView。</summary>
    /// <param name="initialUri">初始 URL（壳 origin；null = 代理绑定失败，窗口留空。G0 不实现
    /// wwwroot 降级；引导页与失败面留 G1）。</param>
    /// <param name="devTools">是否开启 WebView 调试器（<c>DSH_DEVTOOLS=1</c>，与 dev 判定无关）。</param>
    public ShellWindow(Uri? initialUri, bool devTools)
    {
        _origin = initialUri?.GetLeftPart(UriPartial.Authority) ?? string.Empty;
        Title = "DeepSeek Harness";
        Width = 1200;
        Height = 800;

        var webView = new NativeWebView();
        webView.EnvironmentRequested += (_, e) => e.EnableDevTools = devTools;
        webView.NavigationStarted += (_, e) =>
        {
            if (e.Request is { } url && !IsShellNavigation(url))
            {
                e.Cancel = true;
            }
        };
        webView.NewWindowRequested += (_, e) => e.Handled = true;
        if (initialUri is not null)
        {
            webView.Source = initialUri;
        }

        Content = webView;
    }

    private bool IsShellNavigation(Uri url) =>
        string.Equals(url.GetLeftPart(UriPartial.Authority), _origin, StringComparison.OrdinalIgnoreCase);
}
