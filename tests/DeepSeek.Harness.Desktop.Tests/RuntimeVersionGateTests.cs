using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>启动版本底线检查的纯逻辑：--version 输出解析与底线比较。</summary>
public class RuntimeVersionGateTests
{
    [Theory]
    [InlineData("0.1.1-rc.2\n", "0.1.1-rc.2")]
    [InlineData("v0.1.0-rc.8", "0.1.0-rc.8")]
    [InlineData("dsh 1.2.3 (build 42)\n", "1.2.3")]
    [InlineData("\nnoise lines\nthen 0.2.0 here\n", "0.2.0")]
    public void TryParseVersionOutput_ExtractsFirstVersionToken(string output, string expected)
    {
        Assert.Equal(expected, RuntimeVersionGate.TryParseVersionOutput(output));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("no version here", null)]
    public void TryParseVersionOutput_ReturnsNull_WhenNoToken(string output, string? expected)
    {
        Assert.Equal(expected, RuntimeVersionGate.TryParseVersionOutput(output));
    }

    [Theory]
    [InlineData("0.1.0-rc.8", true)]
    [InlineData("0.1.0", true)]
    [InlineData("0.1.1-rc.2", false)]
    [InlineData("0.1.1-rc.1", false)] // 数字核相同：预发布后缀不参与比较，粗粒度防线（文档化语义）
    [InlineData("0.2.0", false)]
    [InlineData("1.0.0", false)]
    public void IsBelowFloor_ComparesNumericSegmentsOnly(string version, bool expected)
    {
        Assert.Equal(expected, RuntimeVersionGate.IsBelowFloor(version));
    }

    [Fact]
    public void IsBelowFloor_MalformedVersion_FailsLoud()
    {
        // 解析出的 token 理论上恒合法；若未来正则放宽导致脏串漏入，宁可抛也不静默放行
        Assert.Throws<ArgumentException>(() => RuntimeVersionGate.IsBelowFloor("not-a-version"));
    }

    [Fact]
    public void BelowFloorBannerScript_MentionsDetectedAndFloorVersions()
    {
        string script = RuntimeVersionGate.BelowFloorBannerScript("0.1.0-rc.8");
        Assert.Contains("0.1.0-rc.8", script);
        Assert.Contains(RuntimeVersionGate.MinimumVersion, script);
        Assert.Contains("var id='dsh-desktop-version-floor-banner'", script);
    }

    [Fact]
    public void BelowFloorBannerScript_LocalizesEnglish()
    {
        // 宿主横幅双语（ADR host-ui-locale）：en 出英文文案与 OK 按钮
        var en = new UiLocale();
        en.Set("en");
        string script = RuntimeVersionGate.BelowFloorBannerScript("0.1.0-rc.8", en);
        Assert.Contains("below the minimum supported", script);
        Assert.Contains("textContent=\"OK\"", script);
        Assert.DoesNotContain("低于桌面支持的最低版本", script);
    }
}
