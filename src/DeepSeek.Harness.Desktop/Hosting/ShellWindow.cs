using Avalonia.Controls;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>G1 主窗：单页承载包自带 <see cref="NativeWebView"/>，加载壳 origin（回环代理）。
/// 关窗拦截在 <see cref="ShellApplication"/>（托盘就绪才隐藏），本类只提供隐藏/唤回的窗口侧动作。
/// 无边框顶栏与失败面留 G1 后续片（ADR proposed/architecture/2026-10-22-avalonia-host-migration）。</summary>
internal sealed class ShellWindow : Window
{
    private readonly string _origin;
    private int _maximizedAtHide = -1;

    /// <summary>创建主窗并挂载 WebView。</summary>
    /// <param name="initialUri">初始 URL（壳 origin；null = 代理绑定失败，窗口留空。wwwroot 降级
    /// 与引导页仍留 G1 后续片）。</param>
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

    private bool IsShellNavigation(Uri url) =>
        string.Equals(url.GetLeftPart(UriPartial.Authority), _origin, StringComparison.OrdinalIgnoreCase);
}
