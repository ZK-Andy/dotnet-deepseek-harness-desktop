using System.Text.Json;
using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>旧 home 一次性提示横幅脚本（纯函数）：路径转义、幂等守卫、关闭按钮。</summary>
public class LegacyHomeNoticeTests
{
    [Fact]
    public void BannerScript_EmbedsJsonEscapedPaths_AndCloseButton()
    {
        // Windows 形态路径含反斜杠与盘符：必须经 JSON 转义后出现在脚本里
        // （路径嵌在整句文案的单一 JSON 字符串内，无独立引号边界，故剥掉外层引号再断言）
        var script = LegacyHomeNotice.BannerScript(
            @"C:\Users\someone\AppData\Local\DeepSeek.Harness.Desktop\dsh",
            "/home/someone/.dsh");

        var escapedWindows = JsonSerializer.Serialize(@"C:\Users\someone\AppData\Local\DeepSeek.Harness.Desktop\dsh").Trim('"');
        Assert.Contains(escapedWindows, script);
        Assert.Contains("/home/someone/.dsh", script);
        // 告知回退通道（环境变量名）写进文案
        Assert.Contains("DSH_DESKTOP_DSH_HOME", script);
        Assert.Contains("知道了", script);
    }

    [Fact]
    public void BannerScript_GuardsDoubleInjection_ByElementId()
    {
        var script = LegacyHomeNotice.BannerScript("/legacy", "/new");
        Assert.Contains("var id='dsh-desktop-legacy-home-banner'", script);
        Assert.Contains("if(document.getElementById(id))return;", script);
    }

    [Fact]
    public void BannerScript_StacksBelowVersionFloorBanner_WhenPresent()
    {
        var script = LegacyHomeNotice.BannerScript("/legacy", "/new");
        Assert.Contains("dsh-desktop-version-floor-banner", script);
        Assert.Contains("b.style.top='44px'", script);
    }
}
