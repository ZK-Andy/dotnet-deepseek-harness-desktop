using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>版本比较的边界：v 前缀、缺段补 0、预发布后缀截断、非法输入 fail loud。</summary>
public class UpdateVersionTests
{
    /// <summary>验证段级比较语义：v 前缀等价、缺段补 0、预发布后缀截断，返回符号符合预期。</summary>
    [Theory]
    [InlineData("0.1.9", "0.1.20", -1)]
    [InlineData("v0.1.20", "0.1.20", 0)]
    [InlineData("0.1.21-rc.1", "0.1.20", 1)]
    [InlineData("0.2.0", "0.1.99", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    public void Compare_Segments(string a, string b, int expectedSign)
    {
        int result = UpdateVersion.Compare(a, b);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    /// <summary>验证非法版本串（非数字段、空串、连续点）在任一参数位置都抛出 ArgumentException。</summary>
    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData("0.a.3")]
    [InlineData("1..2")]
    public void Compare_Throws_OnUnparseable(string bad)
    {
        Assert.Throws<ArgumentException>(() => UpdateVersion.Compare(bad, "0.1.20"));
        Assert.Throws<ArgumentException>(() => UpdateVersion.Compare("0.1.20", bad));
    }
}
