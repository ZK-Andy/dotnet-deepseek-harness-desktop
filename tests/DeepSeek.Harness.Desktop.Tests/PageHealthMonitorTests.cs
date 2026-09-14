namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>探针文本解析（Presentation 桥面）：容忍双引号与首尾空白，空串或垃圾值归为 Unknown。</summary>
public class PageHealthMonitorTests
{
    /// <summary>验证探针文本解析容忍双引号与首尾空白，空串或垃圾值归为 Unknown。</summary>
    [Theory]
    [InlineData("alive", PageHealth.Alive)]
    [InlineData("\"alive\"", PageHealth.Alive)]
    [InlineData(" dead ", PageHealth.Dead)]
    [InlineData("", PageHealth.Unknown)]
    [InlineData(null, PageHealth.Unknown)]
    [InlineData("garbage", PageHealth.Unknown)]
    public void Parse_ToleratesQuotesAndWhitespace(string? raw, PageHealth expected)
    {
        Assert.Equal(expected, PageHealthMonitor.Parse(raw));
    }
}
