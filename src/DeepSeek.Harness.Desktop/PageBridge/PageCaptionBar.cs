
namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// 无边框顶栏的注入单点（ADR 2026-10-23-avalonia-caption-bar）：脚本构建与求值都在这里，
/// <see cref="ShellBridge"/> 只在「导航完成」与「窗口确定启用顶栏」两个时机调用。
/// 失败只留痕——顶栏是增强面，不能拖垮页面；脚本自带幂等守卫，重放不会叠条。
/// </summary>
internal static class PageCaptionBar
{
    /// <summary>按新宿主通道构建顶栏脚本。</summary>
    /// <param name="captionHeightPx">顶栏高度（CSS 像素）。</param>
    /// <param name="uiLocale">UI 语言回落（脚本执行期按 <c>html[lang]</c> 现取）。</param>
    /// <param name="macChrome">是否 macOS 形态。</param>
    /// <param name="edgeResize">宿主是否提供边缘缩放通道（macOS 由窗口系统代劳，页面不得抢边）。</param>
    /// <param name="maximized">注入时窗口是否最大化（页面通道初值，运行时由宿主推新）。</param>
    /// <returns>注入脚本原文。</returns>
    public static string Script(int captionHeightPx, UiLocale? uiLocale, bool macChrome, bool edgeResize, bool maximized = false) =>
        CaptionBar.Build(captionHeightPx, uiLocale, macChrome, edgeResize, maximized);

    /// <summary>对当前文档注入顶栏脚本。</summary>
    /// <param name="surface">网页视图可测面。</param>
    /// <param name="captionHeightPx">顶栏高度（CSS 像素）。</param>
    /// <param name="uiLocale">UI 语言回落。</param>
    /// <param name="macChrome">是否 macOS 形态。</param>
    /// <param name="edgeResize">宿主是否提供边缘缩放通道。</param>
    /// <param name="maximized">注入时窗口是否最大化（页面通道初值，运行时由宿主推新）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="log">日志委托。</param>
    /// <returns>完成的任务。</returns>
    public static async Task InjectAsync(
        IWebViewSurface surface, int captionHeightPx, UiLocale? uiLocale, bool macChrome, bool edgeResize, bool maximized, CancellationToken ct, Action<string> log)
    {
        try
        {
            await surface.InvokeScriptAsync(Script(captionHeightPx, uiLocale, macChrome, edgeResize, maximized), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退出/关窗。
        }
        catch (Exception ex)
        {
            log($"[shell-bridge] 顶栏注入失败：{ex.Message}");
        }
    }
}
