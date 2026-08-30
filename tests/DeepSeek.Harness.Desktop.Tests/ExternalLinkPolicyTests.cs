using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>外部链接判定策略：站外/带 _blank 的 http(s) 应外部打开，站内/非 http(s)/空值应放行。</summary>
public class ExternalLinkPolicyTests
{
    private const string Origin = "http://127.0.0.1:41449";

    /// <summary>验证站外绝对 http(s) 链接判定为应外部打开，且输出解析后的 Uri。</summary>
    [Theory]
    [InlineData("https://x.com/some/status", true)]
    [InlineData("http://example.com/path", true)]
    [InlineData("https://github.com/anysearch-team/anysearch-dsh", true)]
    public void IsExternalHttpLink_ExternalAbsoluteHttp_ReturnsTrue(string href, bool expected)
    {
        Assert.Equal(expected, ExternalLinkPolicy.IsExternalHttpLink(href, Origin, out Uri? url));
        if (expected)
        {
            Assert.NotNull(url);
        }
    }

    /// <summary>验证与 Origin 同源的链接（路径/哈希不同）不被判为外部打开。</summary>
    [Theory]
    [InlineData("http://127.0.0.1:41449/app", false)]
    [InlineData("http://127.0.0.1:41449/#/conversation", false)]
    public void IsExternalHttpLink_SameOrigin_ReturnsFalse(string href, bool expected)
    {
        Assert.Equal(expected, ExternalLinkPolicy.IsExternalHttpLink(href, Origin, out _));
    }

    /// <summary>验证 null/空白、相对路径与非 http(s) scheme（mailto/javascript/data/ryn）一律不判为外部链接。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/relative/path")]
    [InlineData("relative")]
    [InlineData("mailto:a@b.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("ryn://app/index.html")]
    public void IsExternalHttpLink_NonHttpOrRelativeOrEmpty_ReturnsFalse(string? href)
    {
        Assert.False(ExternalLinkPolicy.IsExternalHttpLink(href, Origin, out _));
    }

    /// <summary>验证 scheme 差异、默认端口等价、显式非默认端口与缺失来源等边界下外部链接判定正确。</summary>
    [Theory]
    [InlineData("https://example.com", "https://example.com", false)]
    [InlineData("http://example.com", "https://example.com", true)]  // 不同 scheme
    [InlineData("https://example.com:443/x", "https://example.com", false)] // 默认端口等价
    [InlineData("https://example.com:8443/x", "https://example.com", true)] // 显式非默认端口
    [InlineData("http://example.com", null, true)] // 无来源信息
    public void IsExternalHttpLink_OriginEdgeCases(string href, string? origin, bool expected)
    {
        Assert.Equal(expected, ExternalLinkPolicy.IsExternalHttpLink(href, origin, out _));
    }
}
