using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>壳铸币与请求转发（对齐上游 web-document.ts）：条件回环模拟 dsh 门
/// （裸 / 401；正确 token 303 + 铸 cookie；凭 cookie 200；错 token 401；/echo 回体），
/// 断言铸币/转发/303 跟进/改写/零泄漏/取消语义。</summary>
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

    /// <summary>转发：壳 path 原样映射 + 凭记住的 cookie 200 + 体一致。</summary>
    [Fact]
    public async Task Forward_AfterMint_ServesUiWithMappedPath()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));
        Assert.True(await forward.MintAsync(url, _ => { }, cts.Token));

        DshShellForward.ForwardResult result =
            await forward.ForwardAsync("GET", new Uri("dsh-app://app/chat/?x=1"), null, null, _ => { });

        Assert.Equal(200, result.Status);
        Assert.Equal("text/html; charset=utf-8", result.ContentType);
        Assert.Contains("/chat/?x=1", Encoding.ASCII.GetString(result.Body), StringComparison.Ordinal);
    }

    /// <summary>未铸币即转发：502 小体，不抛（降级面按错误页处理）；loud 一行定音。</summary>
    [Fact]
    public async Task Forward_WithoutMint_Returns502WithoutThrowing()
    {
        var forward = new DshShellForward();
        var lines = new List<string>();

        DshShellForward.ForwardResult result =
            await forward.ForwardAsync("GET", new Uri("dsh-app://app/"), null, null, lines.Add);

        Assert.Equal(502, result.Status);
        Assert.Contains(lines, l => l.Contains("未铸币"));
    }

    /// <summary>POST 体透传：/echo 原样回体（流式以外的方法/体面）。</summary>
    [Fact]
    public async Task Forward_PostBody_EchoedBack()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));
        Assert.True(await forward.MintAsync(url, _ => { }, cts.Token));

        byte[] payload = Encoding.ASCII.GetBytes("hello-forward");
        DshShellForward.ForwardResult result = await forward.ForwardAsync(
            "POST", new Uri("dsh-app://app/echo"), payload, new Dictionary<string, string> { ["X-Probe"] = "1" }, _ => { });

        Assert.Equal(200, result.Status);
        Assert.Equal("hello-forward", Encoding.ASCII.GetString(result.Body));
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

    /// <summary>外链 303 即停 502：跟进守卫只跟回壳的重写 Location，不贴 cookie 去第三方。</summary>
    [Fact]
    public async Task Forward_ExternalRedirect_StopsWith502()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));
        Assert.True(await forward.MintAsync(url, _ => { }, cts.Token));
        var lines = new List<string>();

        DshShellForward.ForwardResult result = await forward.ForwardAsync(
            "GET", new Uri("dsh-app://app/goto-ext"), null, null, lines.Add);

        Assert.Equal(502, result.Status);
        Assert.Contains(lines, l => l.Contains("外链"));
    }

    /// <summary>自循环 303 耗尽跳数即 loud 502，不把裸 3xx 交给页面。</summary>
    [Fact]
    public async Task Forward_RedirectLoop_StopsWith502()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));
        Assert.True(await forward.MintAsync(url, _ => { }, cts.Token));
        var lines = new List<string>();

        DshShellForward.ForwardResult result = await forward.ForwardAsync(
            "GET", new Uri("dsh-app://app/loop"), null, null, lines.Add);

        Assert.Equal(502, result.Status);
        Assert.Contains(lines, l => l.Contains("跟进终止"));
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

    /// <summary>转发 loud 行：入口记方法/path/头体量，终态记状态/类型/字节数（handler 未被调 vs 回包问题的定音判据）；值零泄漏。</summary>
    [Fact]
    public async Task Forward_AfterMint_EmitsEntryAndExitLoudLines()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));
        Assert.True(await forward.MintAsync(url, _ => { }, cts.Token));
        var lines = new List<string>();

        DshShellForward.ForwardResult result = await forward.ForwardAsync(
            "GET", new Uri("dsh-app://app/chat/?x=1"), null, null, lines.Add);

        Assert.Equal(200, result.Status);
        Assert.Contains(lines, l => l.Contains("[shell] 转发：GET /chat/?x=1"));
        Assert.Contains(lines, l => l.Contains("[shell] 转发放回：200") && l.Contains("/chat/?x=1"));
        Assert.DoesNotContain(lines, l => l.Contains(GoodToken) || l.Contains(CookieValue));
    }

    /// <summary>抛固定异常文本的桩传输（脱敏单测用）。</summary>
    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(message);
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

    /// <summary>dsh 门摹拟应答者：裸 / 凭 cookie 200 否则 401；正确 token 303 + 铸 cookie；
    /// 错 token 401；/echo 回体（方法/体透传面）。</summary>
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
                    bool hasCookie = head.Contains($"Cookie: {CookieName}={CookieValue}", StringComparison.Ordinal)
                        || head.Contains($"Cookie: {CookieName}={CookieValue};", StringComparison.Ordinal)
                        || head.Contains($"; {CookieName}={CookieValue}", StringComparison.Ordinal);
                    string response = Route(requestLine, head, hasCookie);
                    byte[] bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes, ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
                {
                    // 单连接收尾：继续服务下一条。
                }
            }
        }

        private static string Route(string requestLine, string head, bool hasCookie)
        {
            if (requestLine.StartsWith("GET /?token=" + GoodToken + " ", StringComparison.Ordinal))
            {
                return LoopbackHttpResponder.Response(
                    "HTTP/1.1 303 See Other",
                    "",
                    $"Location: ./\r\nSet-Cookie: {CookieName}={CookieValue}; Max-Age=99; Path=/; HttpOnly; SameSite=Strict\r\n");
            }

            if (requestLine.StartsWith("POST /echo ", StringComparison.Ordinal))
            {
                int bodyAt = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                string echo = bodyAt >= 0 ? head[(bodyAt + 4)..].TrimEnd('\0') : string.Empty;
                return LoopbackHttpResponder.Response("HTTP/1.1 200 OK", echo, "Content-Type: text/plain\r\n");
            }

            if (requestLine.StartsWith("GET / ", StringComparison.Ordinal)
                || (requestLine.StartsWith("GET /?", StringComparison.Ordinal) && hasCookie))
            {
                if (hasCookie)
                {
                    return LoopbackHttpResponder.Response("HTTP/1.1 200 OK", "UI for " + requestLine, "Content-Type: text/html; charset=utf-8\r\n");
                }

                return LoopbackHttpResponder.Response("HTTP/1.1 401 Unauthorized", "dsh web authentication required\n");
            }

            if (requestLine.StartsWith("GET /chat/", StringComparison.Ordinal) && hasCookie)
            {
                return LoopbackHttpResponder.Response("HTTP/1.1 200 OK", "UI for " + requestLine, "Content-Type: text/html; charset=utf-8\r\n");
            }

            if (requestLine.StartsWith("GET /goto-ext ", StringComparison.Ordinal) && hasCookie)
            {
                return LoopbackHttpResponder.Response("HTTP/1.1 303 See Other", "", "Location: https://example.test/landing\r\n");
            }

            if (requestLine.StartsWith("GET /loop ", StringComparison.Ordinal) && hasCookie)
            {
                return LoopbackHttpResponder.Response("HTTP/1.1 303 See Other", "", "Location: /loop\r\n");
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
