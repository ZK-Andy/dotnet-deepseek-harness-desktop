using System.Runtime.Versioning;

namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>
/// Windows WebView2 启动前置的装配（ADR avalonia-g4-webview2-preflight）：平台探测、系统浏览器与
/// 原生模态在这里构造，<see cref="AvaloniaHost"/> 只调用本方法。Windows 专用——调用方以
/// <c>OperatingSystem.IsWindows()</c> 守卫后再调用。
/// </summary>
internal static class WebView2Registration
{
    /// <summary>探测运行时；缺失时提示（是 = 打开下载页）并返回 false。</summary>
    /// <param name="locale">UI 语言单点。</param>
    /// <returns>true = 运行时可用；false = 缺失（已提示），调用方以非零码退出。</returns>
    [SupportedOSPlatform("windows")]
    internal static bool EnsureRuntimeAvailable(UiLocale locale) =>
        WebView2StartupGuard.EnsureAvailable(
            new WebView2RuntimeProbe(),
            new SystemBrowserService(),
            locale,
            WindowsMessageBox.AskYes,
            HostLog.Write);
}
