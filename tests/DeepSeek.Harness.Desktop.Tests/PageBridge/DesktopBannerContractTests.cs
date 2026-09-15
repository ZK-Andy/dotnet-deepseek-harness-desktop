namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>宿主横幅工厂契约（B2 拆分后的版本底线/未受控退出横幅构建面）：版本/文案/幂等 id 内嵌正确。</summary>
public class DesktopBannerContractTests
{
    /// <summary>验证 en 语言下未受控退出横幅输出英文文案与 textContent="OK" 按钮，不残留中文。</summary>
    [Fact]
    public void UncleanExitBanner_LocalizesEnglish()
    {
        // 宿主横幅双语（ADR host-ui-locale）：en 出英文文案与 OK 按钮
        var en = new UiLocale();
        en.Set("en");
        string script = DesktopBanner.BuildUncleanExitBanner(en);
        Assert.Contains("did not exit cleanly", script);
        Assert.Contains("textContent=\"OK\"", script);
        Assert.DoesNotContain("上次运行未正常退出", script);
    }

    /// <summary>验证生成的横幅脚本同时内嵌检测到的版本、最低版本常量与版本底线横幅标记 id。</summary>
    [Fact]
    public void VersionFloorBanner_MentionsDetectedAndFloorVersions()
    {
        string script = DesktopBanner.BuildVersionFloorBanner("0.1.0-rc.8");
        Assert.Contains("0.1.0-rc.8", script);
        Assert.Contains(RuntimeVersionGate.MinimumVersion, script);
        Assert.Contains("var id='dsh-desktop-version-floor-banner'", script);
    }

    /// <summary>验证 en 语言下横幅输出英文文案与 OK 按钮文本，不含中文文案（双语宿主横幅）。</summary>
    [Fact]
    public void VersionFloorBanner_LocalizesEnglish()
    {
        // 宿主横幅双语（ADR host-ui-locale）：en 出英文文案与 OK 按钮
        var en = new UiLocale();
        en.Set("en");
        string script = DesktopBanner.BuildVersionFloorBanner("0.1.0-rc.8", en);
        Assert.Contains("below the minimum supported", script);
        Assert.Contains("textContent=\"OK\"", script);
        Assert.DoesNotContain("低于桌面支持的最低版本", script);
    }
}
