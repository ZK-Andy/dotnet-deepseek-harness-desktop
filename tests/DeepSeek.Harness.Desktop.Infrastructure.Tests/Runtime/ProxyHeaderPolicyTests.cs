namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>代理源头改写策略（安全不变量：页源零透传，ADR restructure-proxy-header-policy）：
/// 页面侧提交的 Origin/Referer 不得出现在任一出口面，两出口面（HttpClient 转发 / 裸 TCP 隧道）
/// 的自源形态必须一致。钉回归锁「策略漂移改一面漏一面」的事故形态。</summary>
public class ProxyHeaderPolicyTests
{
    /// <summary>源头头名判定大小写不敏感（HTTP 头名不保大小写），其余头不误伤。</summary>
    [Fact]
    public void IsPageSourceHeader_MatchesCaseInsensely()
    {
        Assert.True(ProxyHeaderPolicy.IsPageSourceHeader("Origin"));
        Assert.True(ProxyHeaderPolicy.IsPageSourceHeader("origin"));
        Assert.True(ProxyHeaderPolicy.IsPageSourceHeader("ORIGIN"));
        Assert.True(ProxyHeaderPolicy.IsPageSourceHeader("Referer"));
        Assert.True(ProxyHeaderPolicy.IsPageSourceHeader("reFeReR"));

        Assert.False(ProxyHeaderPolicy.IsPageSourceHeader("Host"));
        Assert.False(ProxyHeaderPolicy.IsPageSourceHeader("Cookie"));
        Assert.False(ProxyHeaderPolicy.IsPageSourceHeader("X-Origin-Probe"));
        Assert.False(ProxyHeaderPolicy.IsPageSourceHeader("Referer-Extra"));
    }

    /// <summary>自源形态：Origin = authority；Referer = authority + 原样 path/query（与 dsh 直出同形）。</summary>
    [Fact]
    public void SelfSource_FormsAuthorityPlusPath()
    {
        (string origin, string referer) =
            ProxyHeaderPolicy.SelfSource("http://127.0.0.1:9", "/chat?x=1");

        Assert.Equal("http://127.0.0.1:9", origin);
        Assert.Equal("http://127.0.0.1:9/chat?x=1", referer);
    }

    /// <summary>转发面钉回归：恶意页源头（外源 Origin/Referer + 大小写变体）零透传，
    /// 出口 Origin/Referer 为 dsh 自源形态。</summary>
    [Fact]
    public void BuildForwardRequest_PageSourceHeaders_NeverPassthrough()
    {
        var headers = new Dictionary<string, string>
        {
            ["ORIGIN"] = "http://evil.example",
            ["Referer"] = "http://evil.example/steal?token=SECRET",
            ["X-Probe"] = "1",
        };

        using HttpRequestMessage request = DshShellForward.BuildForwardRequest(
            "POST", "http://127.0.0.1:9/rpc", null, headers, cookie: null);

        Assert.Equal(["1"], request.Headers.GetValues("X-Probe"));
        Assert.Equal(["http://127.0.0.1:9"], request.Headers.GetValues("Origin"));
        Assert.Equal(["http://127.0.0.1:9/rpc"], request.Headers.GetValues("Referer"));
        Assert.DoesNotContain(request.Headers, h =>
            h.Value.Any(v => v.Contains("evil.example") || v.Contains("SECRET")));
    }

    /// <summary>隧道面钉回归（裸 TCP 重放无 HttpClient 兜底）：恶意页源头零透传；
    /// Host 必写（网关先判 Host/Origin 门）；Origin/Referer 与转发面同一自源形态；cookie 贴壳值。</summary>
    [Fact]
    public void BuildUpgradeHead_PageSourceHeaders_NeverPassthrough()
    {
        var req = new ShellProxyFraming.PageRequest(
            "GET",
            "/channel",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Origin"] = "http://evil.example",
                ["REFERER"] = "http://evil.example/steal?token=SECRET",
                ["Host"] = "page.example",
                ["Cookie"] = "page-cookie=1",
                ["Sec-Fetch-Site"] = "cross-site",
                ["Upgrade"] = "websocket",
            },
            Body: null);

        string head = DshLoopbackTunnel.BuildUpgradeHead(
            req, new Uri("http://127.0.0.1:9"), "shell-cookie=2");

        Assert.DoesNotContain("evil.example", head);
        Assert.DoesNotContain("SECRET", head);
        Assert.DoesNotContain("page-cookie=1", head);
        Assert.DoesNotContain("page.example", head);
        Assert.Contains("Host: 127.0.0.1:9\r\n", head);
        Assert.Contains("Origin: http://127.0.0.1:9\r\n", head);
        Assert.Contains("Referer: http://127.0.0.1:9/channel\r\n", head);
        Assert.Contains("sec-fetch-site: same-origin\r\n", head);
        Assert.Contains("Cookie: shell-cookie=2\r\n", head);
        Assert.Contains("Upgrade: websocket\r\n", head);
    }
}
