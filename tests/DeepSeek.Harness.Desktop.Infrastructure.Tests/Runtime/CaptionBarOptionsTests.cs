using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>CaptionBarOptions 装载（ADR frameless-uniform-caption-bar）：默认值锁定 = 官方 caption
/// 规格（WINDOWS_TITLEBAR_HEIGHT = 40）；缺节/坏 JSON/非正值回退与 RuntimeTimeouts 同哲学。</summary>
public class CaptionBarOptionsTests
{
    /// <summary>验证默认高度 = 官方 Windows caption 规格 40px。</summary>
    [Fact]
    public void Defaults_MatchOfficialCaptionHeight()
    {
        Assert.Equal(40, new CaptionBarOptions().HeightPx);
    }

    /// <summary>验证无 CaptionBar 键或节形态不符时返回全默认值。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"CaptionBar":null}""")]
    [InlineData("""{"CaptionBar":[]}""")]
    [InlineData("""{"Other":1}""")]
    public void Parse_MissingSection_ReturnsDefaults(string json)
    {
        Assert.Equal(40, CaptionBarOptions.Parse(json).HeightPx);
    }

    /// <summary>验证合法节覆盖高度；非正值（无法承载按钮/拖拽条）视作缺失回退默认。</summary>
    [Theory]
    [InlineData("""{"CaptionBar":{"HeightPx":48}}""", 48)]
    [InlineData("""{"CaptionBar":{"HeightPx":0}}""", 40)]
    [InlineData("""{"CaptionBar":{"HeightPx":-3}}""", 40)]
    [InlineData("""{"CaptionBar":{"HeightPx":12.5}}""", 40)]
    public void Parse_HeightOverride_WithNonPositiveFallback(string json, int expected)
    {
        Assert.Equal(expected, CaptionBarOptions.Parse(json).HeightPx);
    }

    /// <summary>验证坏 JSON 由 Parse 抛 JsonException——Load 调用方的 catch 兜底转全默认。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{not-json")]
    public void Parse_BrokenJson_ThrowsJsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => CaptionBarOptions.Parse(json));
    }
}
