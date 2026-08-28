using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>DesktopBanner 工厂契约：幂等 id 守卫、已知横幅运行时堆叠、配色与文案注入。</summary>
public class DesktopBannerTests
{
    private static readonly DesktopBanner.Palette Palette = new("#111111", "#eeeeee", "#222222", "#333333");

    [Fact]
    public void Build_GuardsDoubleInjection()
    {
        var script = DesktopBanner.Build("dsh-desktop-test-banner", "文本", Palette);
        Assert.Contains("var id='dsh-desktop-test-banner'", script);
        Assert.Contains("if(document.getElementById(id))return;", script);
    }

    [Fact]
    public void Build_EmbedsAllKnownIds_ForStackCount()
    {
        var script = DesktopBanner.Build("dsh-desktop-test-banner", "文本", Palette);
        foreach (var known in DesktopBanner.KnownIds)
        {
            Assert.Contains($"'{known}'", script);
        }

        // 堆叠偏移 = 已存横幅数 × 44px（运行时计算，消除各横幅各自手写守卫链的漂移）
        Assert.Contains("top:'+(n*44)+'px;", script);
    }

    [Fact]
    public void Build_EncodesTextViaJsString()
    {
        var script = DesktopBanner.Build("dsh-desktop-test-banner", "包含\"引号\"与</div>", Palette);
        // 文案必须经 JsString 管线，不得直接拼进脚本
        Assert.DoesNotContain("包含</div>", script);
    }

    [Fact]
    public void Build_AppliesPalette()
    {
        var script = DesktopBanner.Build("dsh-desktop-test-banner", "文本", Palette);
        Assert.Contains("background:#111111", script);
        Assert.Contains("color:#eeeeee", script);
        Assert.Contains("#333333", script);
    }
}
