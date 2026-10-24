using DeepSeek.Harness.Desktop.Core.Platform;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// 启动前置：Windows WebView2 运行时缺失的提示与退出（ADR avalonia-g4-webview2-preflight）。
/// 检测在 Avalonia 生命周期与 dsh spawn 之前——缺运行时会在 <c>NativeWebView</c> 创建处失败，
/// 走到那里既没有可用的壳窗口，也拿不到可操作原因。判定与提示可注入，故不碰 Avalonia/Win32
/// 类型、能 headless 单测；原生模态在 <see cref="WindowsMessageBox"/>，Windows 接线在 <see cref="WebView2Registration"/>。
/// </summary>
internal static class WebView2StartupGuard
{
    /// <summary>WebView2 Evergreen 下载页（安装器提示里的同一入口）。</summary>
    internal const string DownloadPageUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>探测并处置：可用即放行；缺失则提示一次（是 = 打开下载页），一律不放行。</summary>
    /// <param name="probe">运行时探测端口。</param>
    /// <param name="browser">系统浏览器端口（用户选「是」时打开下载页）。</param>
    /// <param name="locale">UI 语言单点。</param>
    /// <param name="askYes">是/否模态（标题、正文 → 是否选「是」）。</param>
    /// <param name="log">日志出口。</param>
    /// <returns>true = 运行时可用，可继续启动；false = 缺失（已提示），调用方须退出。</returns>
    internal static bool EnsureAvailable(
        IWebViewRuntimeProbe probe,
        ISystemBrowser browser,
        UiLocale locale,
        Func<string, string, bool> askYes,
        Action<string> log)
    {
        bool available;
        try
        {
            available = probe.IsAvailable();
        }
        catch (Exception ex)
        {
            // 探测缺陷不能变成启动阻断：按可用继续，窗口若真起不来仍有既有失败面兜底。
            log($"[host] WebView2 运行时探测失败，按已安装处理：{ex.Message}");
            return true;
        }

        if (available)
        {
            return true;
        }

        bool english = locale.IsEnglish;
        bool openDownloadPage;
        try
        {
            openDownloadPage = askYes(UiCopy.WebView2MissingTitle(english), UiCopy.WebView2MissingBody(english));
        }
        catch (Exception ex)
        {
            log($"[host] WebView2 缺失提示无法展示：{ex.Message}");
            return false;
        }

        if (openDownloadPage)
        {
            try
            {
                if (!browser.Open(DownloadPageUrl))
                {
                    log("[host] 打开 WebView2 下载页失败（系统浏览器未启动）");
                }
            }
            catch (Exception ex)
            {
                log($"[host] 打开 WebView2 下载页失败：{ex.Message}");
            }
        }

        log("[host] 缺少 WebView2 运行时，提示后退出（窗口无法渲染）");
        return false;
    }
}
