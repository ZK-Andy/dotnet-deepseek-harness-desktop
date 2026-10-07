namespace DeepSeek.Harness.Desktop.Core.WindowChrome;

/// <summary>
/// 窗口 chrome 的三变量几何（语义源 <c>deepseek-ai/deepseek-harness</c> 的
/// <c>ui-layout/README</c>「Window-chrome seat」条与 <c>AppFrame.module.css</c>；
/// 决策见 ADR <c>proposed/simplification/2026-10-08-window-chrome-depatch-core-ports</c>）：
/// <c>TopClearance</c> 是布局台阶（本壳 = 可调顶带高度）、<c>OverlayTop</c> 是对话框/菜单定位
/// （台阶 + 20px，全屏只留 20px）、<c>ChromeTop</c> 是模态遮罩绘制留空（本壳顶带是窗内页面，恒为 0）。
/// </summary>
public sealed record ChromeInsets
{
    /// <summary>布局台阶（CSS 像素）：本壳 = 可调顶带高度（<c>CaptionBarOptions.HeightPx</c>）；
    /// 上游 darwin 固定 48。</summary>
    public int TopClearancePx { get; init; }

    /// <summary>对话框/菜单顶边距（CSS 像素）：台阶 + 20px；全屏态只留 20px 间隙。</summary>
    public int OverlayTopPx { get; init; }

    /// <summary>模态遮罩绘制留空（CSS 像素）：本壳恒 0——顶带是窗内页面（含 dsh 自己的 tab 条），
    /// 随高度发布会把顶带排除在遮罩绘制之外（<c>fb14241</c> 恒零订正）。</summary>
    public int ChromeTopPx { get; init; }

    /// <summary>台阶之外的对话框附加间隙（CSS 像素）：上游 <c>overlay-top = clearance + 20px</c> 的 20。</summary>
    public const int OverlayTopExtraPx = 20;

    /// <summary>由顶带高度解出三变量（纯函数）。</summary>
    /// <param name="heightPx">顶带高度（CSS 像素）。</param>
    /// <param name="fullscreen">是否全屏态：true 时对话框只留 20px 间隙（上游全屏分支）；
    /// 本壳无全屏状态源，当前调用方恒传 false（<c>data-fullscreen</c> 静态不发——它是运行时状态，
    /// 静态发即对上游 CSS 说谎，见 ADR）。</param>
    /// <returns>解出的三变量几何。</returns>
    public static ChromeInsets Resolve(int heightPx, bool fullscreen = false) => new()
    {
        TopClearancePx = heightPx,
        OverlayTopPx = fullscreen ? OverlayTopExtraPx : heightPx + OverlayTopExtraPx,
        ChromeTopPx = 0,
    };
}
