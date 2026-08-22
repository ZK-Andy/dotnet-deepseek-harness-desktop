using DeepSeek.Harness.Desktop.Services.Update;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>版本比较的边界：v 前缀、缺段补 0、预发布后缀截断、非法输入 fail loud。</summary>
public class UpdateVersionTests
{
    [Theory]
    [InlineData("0.1.9", "0.1.20", -1)]
    [InlineData("v0.1.20", "0.1.20", 0)]
    [InlineData("0.1.21-rc.1", "0.1.20", 1)]
    [InlineData("0.2.0", "0.1.99", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    public void Compare_Segments(string a, string b, int expectedSign)
    {
        var result = UpdateVersion.Compare(a, b);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    public void Compare_Throws_OnUnparseable(string bad)
    {
        Assert.Throws<ArgumentException>(() => UpdateVersion.Compare(bad, "0.1.20"));
        Assert.Throws<ArgumentException>(() => UpdateVersion.Compare("0.1.20", bad));
    }
}
