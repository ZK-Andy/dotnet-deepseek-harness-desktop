namespace DeepSeek.Harness.Desktop.Core.Tests.WindowChrome;

/// <summary>三变量几何（ADR window-chrome-depatch-core-ports）：纯逻辑契约——台阶随高度、
/// 对话框定位 = 台阶 + 20px、全屏只留 20px、遮罩留空恒 0；端口默认实现与记录体同解。</summary>
public class ChromeInsetsTests
{
    /// <summary>验证非全屏映射：台阶 = 高度、对话框 = 高度 + 20、遮罩 = 0（上游 Windows/达尔文回落一致）。</summary>
    [Theory]
    [InlineData(52, 72)]
    [InlineData(48, 68)]
    public void Resolve_Windowed_MapsHeightToThreeVariables(int height, int overlay)
    {
        var insets = ChromeInsets.Resolve(height, fullscreen: false);
        Assert.Equal(height, insets.TopClearancePx);
        Assert.Equal(overlay, insets.OverlayTopPx);
        Assert.Equal(0, insets.ChromeTopPx);
    }

    /// <summary>验证全屏映射：台阶不变、对话框只留 20px 间隙、遮罩归零（上游全屏分支）。</summary>
    [Fact]
    public void Resolve_Fullscreen_KeepsOnlyOverlayGap()
    {
        var insets = ChromeInsets.Resolve(52, fullscreen: true);
        Assert.Equal(52, insets.TopClearancePx);
        Assert.Equal(ChromeInsets.OverlayTopExtraPx, insets.OverlayTopPx);
        Assert.Equal(0, insets.ChromeTopPx);
    }

    /// <summary>验证附加间隙取值不手抄：与上游 overlay-top 形态一致，恒为 20。</summary>
    [Fact]
    public void OverlayTopExtra_IsTwenty()
    {
        Assert.Equal(20, ChromeInsets.OverlayTopExtraPx);
    }

    /// <summary>验证端口默认实现与记录体同解（Theory 两例全覆盖窗口/全屏分支）。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PolicyDefault_ResolvesSameAsRecord(bool fullscreen)
    {
        var expected = ChromeInsets.Resolve(52, fullscreen);
        Assert.Equal(expected, WindowChromePolicy.Default.ResolveInsets(52, fullscreen));
    }
}
