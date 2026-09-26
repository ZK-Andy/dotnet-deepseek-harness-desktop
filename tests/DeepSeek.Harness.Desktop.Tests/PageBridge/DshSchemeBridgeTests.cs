using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>壳 scheme 桥接 handler：命中/回包各一 loud 行（mac 转发验证"handler 没被调还是回包有问题"的一次定音判据），值永不落盘。</summary>
public class DshSchemeBridgeTests
{
    /// <summary>命中与回包各一 loud 行：方法/path/状态可判读，token/cookie 值零泄漏。</summary>
    [Fact]
    public async Task Handler_EmitsHitAndResponseLoudLines()
    {
        var forward = new DshShellForward(new StubDshHandler());
        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri("http://127.0.0.1:9/?token=HANDLERTOKEN")), _ => { }, CancellationToken.None));
        var lines = new List<string>();

        RynSchemeResponse response = await DshSchemeBridge.Handler(forward, lines.Add)(new RynSchemeRequest
        {
            Method = "GET",
            Url = new Uri("dsh-app://app/chat"),
            Headers = new Dictionary<string, string>(),
            Body = ReadOnlyMemory<byte>.Empty,
        });

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(lines, l => l.Contains("[shell] handler 命中：GET /chat"));
        Assert.Contains(lines, l => l.Contains("[shell] handler 回包：200") && l.Contains("GET /chat"));
        Assert.DoesNotContain(lines, l => l.Contains("HANDLERTOKEN") || l.Contains("STUBSECRET"));
    }

    /// <summary>桩 dsh：token 跳 303 + 铸 cookie，其余凭 cookie 200（零 socket 回环）。</summary>
    private sealed class StubDshHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Query.Contains("token=", StringComparison.Ordinal) == true)
            {
                var mint = new HttpResponseMessage(System.Net.HttpStatusCode.SeeOther);
                mint.Headers.TryAddWithoutValidation("Set-Cookie", "test-shell-auth=STUBSECRET; Path=/; HttpOnly");
                return Task.FromResult(mint);
            }

            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("UI"u8.ToArray()),
            };
            ok.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
            return Task.FromResult(ok);
        }
    }
}
