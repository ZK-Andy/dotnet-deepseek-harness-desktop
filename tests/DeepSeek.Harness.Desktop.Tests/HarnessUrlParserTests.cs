using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>HarnessUrlParser 行为：dsh web: 前缀解析。</summary>
public class HarnessUrlParserTests
{
    [Fact]
    public void TryParse_PrefixedLine_ReturnsUri()
    {
        Uri? uri = HarnessUrlParser.TryParse("dsh web: http://127.0.0.1:41989");
        Assert.NotNull(uri);
        Assert.Equal("http://127.0.0.1:41989/", uri!.ToString());
    }

    [Fact]
    public void TryParse_OtherOutputLine_ReturnsNull()
    {
        Assert.Null(HarnessUrlParser.TryParse("some other log line"));
    }

    [Fact]
    public void TryParse_MalformedUrlAfterPrefix_ReturnsNull()
    {
        Assert.Null(HarnessUrlParser.TryParse("dsh web: not a url"));
    }

    [Fact]
    public void TryParse_NullLine_ReturnsNull()
    {
        Assert.Null(HarnessUrlParser.TryParse(null));
    }
}
