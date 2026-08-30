using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>DesktopBanner 工厂契约：幂等 id 守卫、已知横幅运行时堆叠、配色与文案注入。</summary>
public class DesktopBannerTests
{
    private static readonly DesktopBanner.Palette s_palette = new("#111111", "#eeeeee", "#222222", "#333333");

    /// <summary>验证生成脚本带固定 id 的幂等守卫，同一横幅重复注入时直接返回。</summary>
    [Fact]
    public void Build_GuardsDoubleInjection()
    {
        string script = DesktopBanner.Build("dsh-desktop-test-banner", "文本", s_palette);
        Assert.Contains("var id='dsh-desktop-test-banner'", script);
        Assert.Contains("if(document.getElementById(id))return;", script);
    }

    /// <summary>验证脚本内嵌全部已知横幅 id，堆叠偏移按已存横幅数 × 44px 运行时计算。</summary>
    [Fact]
    public void Build_EmbedsAllKnownIds_ForStackCount()
    {
        string script = DesktopBanner.Build("dsh-desktop-test-banner", "文本", s_palette);
        foreach (string known in DesktopBanner.KnownIds)
        {
            Assert.Contains($"'{known}'", script);
        }

        // 堆叠偏移 = 已存横幅数 × 44px（运行时计算，消除各横幅各自手写守卫链的漂移）
        Assert.Contains("top:'+(n*44)+'px;", script);
    }

    /// <summary>验证文案经 JsString 管线转义，含引号与闭合标签的原始输入不会直接拼进脚本。</summary>
    [Fact]
    public void Build_EncodesTextViaJsString()
    {
        string script = DesktopBanner.Build("dsh-desktop-test-banner", "包含\"引号\"与</div>", s_palette);
        // 文案必须经 JsString 管线，不得直接拼进脚本
        Assert.DoesNotContain("包含</div>", script);
    }

    /// <summary>验证传入配色（背景/前景/边框）完整落地为脚本内联样式。</summary>
    [Fact]
    public void Build_AppliesPalette()
    {
        string script = DesktopBanner.Build("dsh-desktop-test-banner", "文本", s_palette);
        Assert.Contains("background:#111111", script);
        Assert.Contains("color:#eeeeee", script);
        Assert.Contains("#333333", script);
    }

    /// <summary>验证按钮文案随宿主 locale 切换：en 出「OK」、缺省中文，且中文经 JsString 转义为大写 \u 序列。</summary>
    [Fact]
    public void Build_OkLabel_LocalizedViaJsString()
    {
        // 按钮文案随宿主 locale（ADR host-ui-locale）：en 出 OK，缺省中文；经 JsString 管线（非 ASCII \u 转义）
        string en = DesktopBanner.Build("dsh-desktop-test-banner", "文本", s_palette, okLabel: "OK");
        Assert.Contains("textContent=\"OK\"", en);

        string zh = DesktopBanner.Build("dsh-desktop-test-banner", "文本", s_palette);
        Assert.DoesNotContain("textContent=\"OK\"", zh);
        // 「知」= U+77E5：JsString 转义后的中文文案（编码器输出大写十六进制）
        Assert.Contains("\\u77E5", zh);
    }
}
