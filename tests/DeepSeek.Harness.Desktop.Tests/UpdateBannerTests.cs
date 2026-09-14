namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>就绪横幅脚本：版本嵌入与幂等守卫。</summary>
public class UpdateBannerTests
{
    /// <summary>验证就绪横幅脚本随宿主 locale 输出英文「is ready」文案与「OK」按钮文本，缺省中文无英文残留。</summary>
    [Fact]
    public void ReadyScript_LocalizesEnglish()
    {
        // 宿主横幅双语（ADR host-ui-locale）：en 出英文文案，缺省中文
        var en = new UiLocale();
        en.Set("en");
        string script = UpdateBanner.ReadyScript("9.9.9", en);
        Assert.Contains("is ready", script);
        Assert.Contains("textContent=\"OK\"", script);
        Assert.DoesNotContain("已就绪", script);
    }

    /// <summary>验证就绪横幅脚本嵌入版本号 9.9.9 并带幂等 id 守卫，重复注入时直接返回不再堆叠。</summary>
    [Fact]
    public void ReadyScript_EmbedsVersion_GuardsDoubleInjection()
    {
        string script = UpdateBanner.ReadyScript("9.9.9");
        Assert.Contains("9.9.9", script);
        Assert.Contains("var id='dsh-desktop-update-ready-banner'", script);
        Assert.Contains("if(document.getElementById(id))return;", script);
    }
}
