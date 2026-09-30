namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>DesktopBanner 工厂契约：幂等 id 守卫、已知横幅运行时堆叠、语义色调映射与文案注入。</summary>
public class DesktopBannerTests
{
    private const DesktopBanner.BannerTone Tone = DesktopBanner.BannerTone.Neutral;

    /// <summary>验证生成脚本带固定 id 的幂等守卫，同一横幅重复注入时直接返回。</summary>
    [Fact]
    public void Build_GuardsDoubleInjection()
    {
        string script = DesktopBanner.Build("dsh-desktop-test-banner", "文本", Tone);
        Assert.Contains("var id='dsh-desktop-test-banner'", script);
        Assert.Contains("if(document.getElementById(id))return;", script);
    }

    /// <summary>验证脚本内嵌全部已知横幅 id，堆叠偏移按已存横幅数 × 44px 运行时计算。</summary>
    [Fact]
    public void Build_EmbedsAllKnownIds_ForStackCount()
    {
        string script = DesktopBanner.Build("dsh-desktop-test-banner", "文本", Tone);
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
        string script = DesktopBanner.Build("dsh-desktop-test-banner", "包含\"引号\"与</div>", Tone);
        // 文案必须经 JsString 管线，不得直接拼进脚本
        Assert.DoesNotContain("包含</div>", script);
    }

    /// <summary>验证表面恒中性（bg-overlay/label-primary/border-l2 token 随主题），按钮按 tone 取语义 token：
    /// Warn=琥珀、Success=绿、Neutral=反色填充。</summary>
    [Fact]
    public void Build_AppliesToneTokens()
    {
        string surface = DesktopBanner.Build("dsh-desktop-test-banner", "文本", Tone);
        Assert.Contains("background:var(--dsw-alias-bg-overlay,#fff)", surface);
        Assert.Contains("color:var(--dsw-alias-label-primary,#0f1111)", surface);
        Assert.Contains("border-bottom:1px solid var(--dsw-alias-border-l2,", surface);
        Assert.Contains("background:var(--dsw-alias-label-primary,#0f1111);color:var(--dsw-alias-bg-overlay,#fff)", surface);

        Assert.Contains("background:var(--dsw-alias-state-warn-primary,#b45309);color:#fff",
            DesktopBanner.Build("dsh-desktop-test-banner", "文本", DesktopBanner.BannerTone.Warn));
        Assert.Contains("background:var(--dsw-alias-state-success-primary,#16a34a);color:#fff",
            DesktopBanner.Build("dsh-desktop-test-banner", "文本", DesktopBanner.BannerTone.Success));
    }

    /// <summary>验证按钮文案随宿主 locale 切换：en 出「OK」、缺省中文，且中文经 JsString 转义为大写 \u 序列。</summary>
    [Fact]
    public void Build_OkLabel_LocalizedViaJsString()
    {
        // 按钮文案随宿主 locale（ADR host-ui-locale）：en 出 OK，缺省中文；经 JsString 管线（非 ASCII \u 转义）
        string en = DesktopBanner.Build("dsh-desktop-test-banner", "文本", Tone, okLabel: "OK");
        Assert.Contains("textContent=\"OK\"", en);

        string zh = DesktopBanner.Build("dsh-desktop-test-banner", "文本", Tone);
        Assert.DoesNotContain("textContent=\"OK\"", zh);
        // 「知」= U+77E5：JsString 转义后的中文文案（编码器输出大写十六进制）
        Assert.Contains("\\u77E5", zh);
    }
}
