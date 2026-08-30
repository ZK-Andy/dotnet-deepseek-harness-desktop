using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>HarnessUrlParser 行为：dsh web: 前缀解析。</summary>
public class HarnessUrlParserTests
{
    /// <summary>验证 "dsh web: &lt;url&gt;" 前缀行能解析出 Uri，且结果补全尾部斜杠。</summary>
    [Fact]
    public void TryParse_PrefixedLine_ReturnsUri()
    {
        Uri? uri = HarnessUrlParser.TryParse("dsh web: http://127.0.0.1:41989");
        Assert.NotNull(uri);
        Assert.Equal("http://127.0.0.1:41989/", uri!.ToString());
    }

    /// <summary>验证普通非前缀输出行返回 null，不会把无关日志误识别为 dsh web URL。</summary>
    [Fact]
    public void TryParse_OtherOutputLine_ReturnsNull()
    {
        Assert.Null(HarnessUrlParser.TryParse("some other log line"));
    }

    /// <summary>验证前缀后跟非法 URL 内容时返回 null 而非抛异常，保证输出流解析不因脏数据崩溃。</summary>
    [Fact]
    public void TryParse_MalformedUrlAfterPrefix_ReturnsNull()
    {
        Assert.Null(HarnessUrlParser.TryParse("dsh web: not a url"));
    }

    /// <summary>验证传入 null 行时安全返回 null，不触发空引用异常。</summary>
    [Fact]
    public void TryParse_NullLine_ReturnsNull()
    {
        Assert.Null(HarnessUrlParser.TryParse(null));
    }
}
