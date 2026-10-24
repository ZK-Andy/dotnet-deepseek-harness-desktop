using DeepSeek.Harness.Desktop.Core.Ipc;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>页面桥域装配（ADR avalonia-g2-shell-bridge）：系统浏览器端口、命令路由表与桥壳在此构造
/// （R1a 的 new 落注册处，装配面只调用）。</summary>
internal static class PageBridgeRegistration
{
    /// <summary>页面桥装配壳：外链路由、语言桥（读写）、伴生降级报障四条路由与壳 origin 判据一次给齐。
    /// 窗口挂载后由 <see cref="Hosting.ShellBridgeHost.Attach"/> 接上；窗口命令在窗口句柄就绪后追加。</summary>
    /// <param name="shellOrigin">壳 origin（代理 URL 的 authority）；null = 代理未绑定。</param>
    /// <param name="uiLocale">宿主 UI 语言单点。</param>
    /// <param name="macChrome">顶栏是否 macOS 形态。</param>
    /// <param name="captionHeightPx">顶栏高度（CSS 像素）。</param>
    /// <param name="log">日志委托。</param>
    /// <returns>页面桥装配壳（窗口挂载后接上）。</returns>
    public static Hosting.ShellBridgeHost CreateShellBridgeHost(
        string? shellOrigin,
        UiLocale uiLocale,
        bool macChrome,
        int captionHeightPx,
        Action<string> log)
    {
        var browser = new SystemBrowserService();
        ICommandRouter[] routers =
        {
            new ExternalLinkCommandRouter(browser, log),
            new UiLocaleCommandRouter(uiLocale),
            new CompanionLocaleCommandRouter(uiLocale, log),
            new CompanionReportCommandRouter(log),
        };
        return new Hosting.ShellBridgeHost(routers, uiLocale, shellOrigin, browser, macChrome, captionHeightPx, log);
    }
}
