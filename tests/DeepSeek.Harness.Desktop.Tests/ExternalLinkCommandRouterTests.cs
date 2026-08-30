using System.Text;
using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>app.openExternal 命令路由：解析 JSON 载荷、框架校验、委托打开器接调用。</summary>
public class ExternalLinkCommandRouterTests
{
    /// <summary>验证路由只匹配 ExternalLinkCommandRouter.CommandName，其他命令名与空串均不可路由。</summary>
    [Fact]
    public void CanRoute_MatchesCommandName()
    {
        var router = new ExternalLinkCommandRouter();
        Assert.True(router.CanRoute(ExternalLinkCommandRouter.CommandName));
        Assert.False(router.CanRoute("window.close"));
        Assert.False(router.CanRoute(""));
    }

    /// <summary>验证合法 http(s) URL 载荷经 JSON 解析后委托 opener 打开，并返回 "{}" 成功帧。</summary>
    [Fact]
    public async Task RouteAsync_OpensValidHttpUrl()
    {
        string? opened = null;
        var router = new ExternalLinkCommandRouter(opener: url => { opened = url; return true; });
        byte[] body = Encoding.UTF8.GetBytes("""{"url":"https://x.com/foo/status/123"}""");

        string result = await router.RouteAsync(
            ExternalLinkCommandRouter.CommandName,
            body,
            services: null!,
            CancellationToken.None);

        Assert.Equal("{}", result);
        Assert.Equal("https://x.com/foo/status/123", opened);
    }

    /// <summary>验证非 http(s) scheme（mailto/javascript）、相对路径、缺 url 字段、非 JSON 与空体一律不触发 opener，返回 null 帧。</summary>
    [Theory]
    [InlineData("""{"url":"mailto:a@b.com"}""")]                  // 非 http(s)
    [InlineData("""{"url":"javascript:alert(1)"}""")]            // 危险 scheme
    [InlineData("""{"url":"/relative"}""")]                      // 相对
    [InlineData("""{"url":"{}""")]                               // 无 url
    [InlineData("""{"foo":1}""")]                                // 无 url 字段
    [InlineData("not-json")]                                     // 非 JSON
    [InlineData("")]                                             // 空体
    public async Task RouteAsync_RejectsUnsafeOrNonHttp(string body)
    {
        bool opened = false;
        var router = new ExternalLinkCommandRouter(opener: _ => { opened = true; return true; });

        string result = await router.RouteAsync(
            ExternalLinkCommandRouter.CommandName,
            Encoding.UTF8.GetBytes(body),
            services: null!,
            CancellationToken.None);

        Assert.False(opened, $"不该打开：{body}");
        Assert.Equal("null", result);
    }

    /// <summary>验证 opener 返回 false（打开被拒）时不抛错，仍返回 null 帧维持 IPC 成功语义。</summary>
    [Fact]
    public async Task RouteAsync_OpenerReturnsFalse_StillReturnsSuccessFrame()
    {
        var router = new ExternalLinkCommandRouter(opener: _ => false);

        string result = await router.RouteAsync(
            ExternalLinkCommandRouter.CommandName,
            Encoding.UTF8.GetBytes("""{"url":"https://example.com"}"""),
            services: null!,
            CancellationToken.None);

        Assert.Equal("null", result);
    }

    /// <summary>验证 opener 抛异常时路由不向外抛错误，返回 null 帧（失败写日志、不向 JS 侧抛 IPC 错误）。</summary>
    [Fact]
    public async Task RouteAsync_OpenerThrows_ReturnsNull_DoesNotThrow()
    {
        var router = new ExternalLinkCommandRouter(opener: _ => throw new InvalidOperationException("no browser"));

        string result = await router.RouteAsync(
            ExternalLinkCommandRouter.CommandName,
            Encoding.UTF8.GetBytes("""{"url":"https://example.com"}"""),
            services: null!,
            CancellationToken.None);

        Assert.Equal("null", result); // 失败写日志、不向 JS 抛 IPC 错误
    }
}
