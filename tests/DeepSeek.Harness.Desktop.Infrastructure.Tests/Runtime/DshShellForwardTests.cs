using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>壳铸币与跟进目标解析（对齐上游 web-document.ts）：条件回环模拟 dsh 门
/// （正确 token 303 + 铸 cookie；错 token 401），断言铸币/零泄漏/取消语义/跟进纯函数。
/// 转发执行面在 <c>DshLoopbackProxy</c>（见 <c>DshLoopbackProxyTests</c>）。</summary>
public class DshShellForwardTests
{
    private const string GoodToken = "SHELLTOKEN123";
    private const string CookieName = "test-shell-auth";
    private const string CookieValue = "SHELLSECRET456";

    /// <summary>铸币存 cookie：303 + 名值记住；日志无秘密。</summary>
    [Fact]
    public async Task Mint_ValidToken_StoresCookieWithoutSecretsInLog()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward();
        var lines = new List<string>();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        bool minted = await forward.MintAsync(url, lines.Add, cts.Token);

        Assert.True(minted);
        Assert.DoesNotContain(lines, l => l.Contains(GoodToken));
        Assert.DoesNotContain(lines, l => l.Contains(CookieValue));
        Assert.Contains(lines, l => l.Contains("303") && l.Contains(CookieName));
    }

    /// <summary>错 token 铸币失败 loud 返回 false，不抛。</summary>
    [Fact]
    public async Task Mint_WrongToken_ReturnsFalseWithoutThrowing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward();
        var lines = new List<string>();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token=WRONG"));

        bool minted = await forward.MintAsync(url, lines.Add, cts.Token);

        Assert.False(minted);
        Assert.DoesNotContain(lines, l => l.Contains("WRONG"));
    }

    /// <summary>取消即上抛（R2 B1）：预取消的令牌使铸币抛，不吞。</summary>
    [Fact]
    public async Task Mint_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var forward = new DshShellForward();
        var url = DshWebUrl.From(new Uri("http://127.0.0.1:9/?token=x"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => forward.MintAsync(url, _ => { }, cts.Token));
    }

    /// <summary>铸币失败消息脱敏：异常消息里的 token 子串替换为 ***（桩传输抛带 token 文本的异常）。</summary>
    [Fact]
    public async Task Mint_FailureMessage_RedactsToken()
    {
        var forward = new DshShellForward(new ThrowingHandler("boom LEAKTOKEN tail"));
        var lines = new List<string>();
        var url = DshWebUrl.From(new Uri("http://127.0.0.1:9/?token=LEAKTOKEN"));

        bool minted = await forward.MintAsync(url, lines.Add, CancellationToken.None);

        Assert.False(minted);
        Assert.DoesNotContain(lines, l => l.Contains("LEAKTOKEN"));
        Assert.Contains(lines, l => l.Contains("***"));
    }

    /// <summary>抛固定异常文本的桩传输（脱敏单测用）。</summary>
    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(message);
    }

    /// <summary>请求构造两级落头：Host/Cookie/Accept-Encoding 剔除（cookie 由壳值覆盖），
    /// Content-Type 落 Content 头，业务头透传（代理与旧转发的共享契约钉死）。</summary>
    [Fact]
    public void BuildForwardRequest_AppliesHeaderPolicy()
    {
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["Host"] = "page.example",
            ["Cookie"] = "page-cookie=1",
            ["Accept-Encoding"] = "gzip",
            ["X-Probe"] = "1",
        };

        using HttpRequestMessage request = DshShellForward.BuildForwardRequest(
            "POST", "http://127.0.0.1:9/rpc", [1, 2, 3], headers, "shell-cookie=2");

        Assert.Equal("1", string.Join(",", request.Headers.GetValues("X-Probe")));
        Assert.False(request.Headers.Contains("Host"));
        Assert.False(request.Headers.Contains("Accept-Encoding"));
        Assert.Equal(["shell-cookie=2"], request.Headers.GetValues("Cookie"));
        Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
    }

    /// <summary>跟进目标解析纯函数：dsh 自指（相对/绝对）回 dsh 形 URL，外链/非法回 null。</summary>
    [Fact]
    public void ResolveFollowTarget_MapsSelfLinksRejectsOthers()
    {
        const string authority = "http://127.0.0.1:9";

        Assert.Equal("http://127.0.0.1:9/", DshShellForward.ResolveFollowTarget("./", authority));
        Assert.Equal("http://127.0.0.1:9/a?b=1", DshShellForward.ResolveFollowTarget("http://127.0.0.1:9/a?b=1", authority));
        Assert.Null(DshShellForward.ResolveFollowTarget("https://example.test/x", authority));
        Assert.Null(DshShellForward.ResolveFollowTarget("nota-url-:::", authority));
    }

    /// <summary>dsh 门摹拟应答者：正确 token 303 + 铸 cookie；其余 401（铸币面最小形态；
    /// 转发执行面的桩见 <c>DshLoopbackProxyTests.StubDshHandler</c>）。</summary>
    private sealed class DshMimicResponder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serving;

        public DshMimicResponder(int port, CancellationToken ct)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _serving = ServeAsync(ct);
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            while (!linked.Token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(linked.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = HandleAsync(client, linked.Token);
            }
        }

        private static async Task HandleAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    byte[] buf = new byte[8192];
                    int read = await stream.ReadAsync(buf, ct);
                    if (read == 0)
                    {
                        return;
                    }

                    string head = Encoding.ASCII.GetString(buf, 0, read);
                    string requestLine = head.Split("\r\n")[0];
                    string response = Route(requestLine);
                    byte[] bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes, ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
                {
                    // 单连接收尾：继续服务下一条。
                }
            }
        }

        private static string Route(string requestLine)
        {
            if (requestLine.StartsWith("GET /?token=" + GoodToken + " ", StringComparison.Ordinal))
            {
                return LoopbackHttpResponder.Response(
                    "HTTP/1.1 303 See Other",
                    "",
                    $"Location: ./\r\nSet-Cookie: {CookieName}={CookieValue}; Max-Age=99; Path=/; HttpOnly; SameSite=Strict\r\n");
            }

            return LoopbackHttpResponder.Response("HTTP/1.1 401 Unauthorized", "dsh web authentication required\n");
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
