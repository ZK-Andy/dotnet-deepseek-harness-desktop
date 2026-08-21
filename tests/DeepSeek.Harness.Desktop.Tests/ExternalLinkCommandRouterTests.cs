using System.Text;
using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>app.openExternal 命令路由：解析 JSON 载荷、框架校验、委托打开器接调用。</summary>
public class ExternalLinkCommandRouterTests
{
    [Fact]
    public void CanRoute_MatchesCommandName()
    {
        var router = new ExternalLinkCommandRouter();
        Assert.True(router.CanRoute(ExternalLinkCommandRouter.CommandName));
        Assert.False(router.CanRoute("window.close"));
        Assert.False(router.CanRoute(""));
    }

    [Fact]
    public async Task RouteAsync_OpensValidHttpUrl()
    {
        string? opened = null;
        var router = new ExternalLinkCommandRouter(opener: url => { opened = url; return true; });
        var body = Encoding.UTF8.GetBytes("""{"url":"https://x.com/foo/status/123"}""");

        var result = await router.RouteAsync(
            ExternalLinkCommandRouter.CommandName,
            body,
            services: null!,
            CancellationToken.None);

        Assert.Equal("{}", result);
        Assert.Equal("https://x.com/foo/status/123", opened);
    }

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

        var result = await router.RouteAsync(
            ExternalLinkCommandRouter.CommandName,
            Encoding.UTF8.GetBytes(body),
            services: null!,
            CancellationToken.None);

        Assert.False(opened, $"不该打开：{body}");
        Assert.Equal("null", result);
    }

    [Fact]
    public async Task RouteAsync_OpenerReturnsFalse_StillReturnsSuccessFrame()
    {
        var router = new ExternalLinkCommandRouter(opener: _ => false);

        var result = await router.RouteAsync(
            ExternalLinkCommandRouter.CommandName,
            Encoding.UTF8.GetBytes("""{"url":"https://example.com"}"""),
            services: null!,
            CancellationToken.None);

        Assert.Equal("null", result);
    }

    [Fact]
    public async Task RouteAsync_OpenerThrows_ReturnsNull_DoesNotThrow()
    {
        var router = new ExternalLinkCommandRouter(opener: _ => throw new InvalidOperationException("no browser"));

        var result = await router.RouteAsync(
            ExternalLinkCommandRouter.CommandName,
            Encoding.UTF8.GetBytes("""{"url":"https://example.com"}"""),
            services: null!,
            CancellationToken.None);

        Assert.Equal("null", result); // 失败写日志、不向 JS 抛 IPC 错误
    }
}
