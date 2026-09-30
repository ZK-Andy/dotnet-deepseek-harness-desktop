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

    /// <summary>测试快参：稳定窗/节拍 1ms（两拍即稳）、预算 250ms（fail-open 路径测试秒级返回）。</summary>
    private static readonly DshShellForward.ReadyStabilization s_fast = new(
        StableWindow: TimeSpan.FromMilliseconds(1),
        PollInterval: TimeSpan.FromMilliseconds(1),
        Budget: TimeSpan.FromMilliseconds(250));

    /// <summary>铸币存 cookie：303 + 名值记住；稳定化探活（携带铸得 cookie）即 Ready；日志无秘密。</summary>
    [Fact]
    public async Task Mint_ValidToken_StoresCookieWithoutSecretsInLog()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var lines = new List<string>();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        bool minted = await forward.MintAsync(url, lines.Add, cts.Token);

        Assert.True(minted);
        Assert.True(server.ProbeCookieSeen, "稳定化探活应携带铸得的 cookie（POST /api/session/list）");
        Assert.DoesNotContain(lines, l => l.Contains(GoodToken));
        Assert.DoesNotContain(lines, l => l.Contains(CookieValue));
        Assert.Contains(lines, l => l.Contains("303") && l.Contains(CookieName));
        Assert.Contains(lines, l => l.Contains("就绪稳定化：会话面 Ready"));
        Assert.DoesNotContain(lines, l => l.Contains("预算耗尽"));
    }

    /// <summary>错 token 铸币失败 loud 返回 false，不抛。</summary>
    [Fact]
    public async Task Mint_WrongToken_ReturnsFalseWithoutThrowing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var lines = new List<string>();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token=WRONG"));

        bool minted = await forward.MintAsync(url, lines.Add, cts.Token);

        Assert.False(minted);
        Assert.DoesNotContain(lines, l => l.Contains("WRONG"));
    }

    /// <summary>稳定化不通过不放行：303 已铸到 cookie 但会话面恒 401，route/TCS 在预算耗尽前不得可见
    /// （holder 不得提前 reload 进半成品页面，ADR holder-mint-gate-deepening）。</summary>
    [Fact]
    public async Task Mint_RouteInvisibleUntilStabilized()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token) { ReadyDelayMilliseconds = 400 };
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        Task<bool> minting = forward.MintAsync(url, _ => { }, cts.Token);
        await Task.Delay(100, cts.Token);

        Assert.False(minting.IsCompleted);
        Assert.False(forward.TryGetRoute(out _, out _), "稳定化未通过前 route 不得可见");

        Assert.True(await minting);
        Assert.True(forward.TryGetRoute(out _, out _));
    }

    /// <summary>延迟就绪（0 &lt; 时延 &lt; 预算）：稳定化真正走「探活 Ready 达稳定窗」放行而非 fail-open
    /// 兜底——日志含稳定化成功标记、无预算耗尽标记（评审补强：区分门控放行与预算兜底放行两条路径）。</summary>
    [Fact]
    public async Task Mint_WebFaceReadyAfterDelay_PassesGateWithoutFailOpen()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token) { ReadyDelayMilliseconds = 100 };
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var lines = new List<string>();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        bool minted = await forward.MintAsync(url, lines.Add, cts.Token);

        Assert.True(minted);
        Assert.True(forward.TryGetRoute(out _, out _));
        Assert.Contains(lines, l => l.Contains("就绪稳定化：会话面 Ready"));
        Assert.DoesNotContain(lines, l => l.Contains("预算耗尽"));
    }

    /// <summary>会话面恒不就绪：预算耗尽 fail-open——仍铸币（返回 true）且日志留痕预算耗尽标记。</summary>
    [Fact]
    public async Task Mint_WebFaceNeverReady_FailOpenAfterBudget()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token) { ReadyDelayMilliseconds = -1 };
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var lines = new List<string>();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        bool minted = await forward.MintAsync(url, lines.Add, cts.Token);

        Assert.True(minted);
        Assert.True(forward.TryGetRoute(out _, out _));
        Assert.Contains(lines, l => l.Contains("预算耗尽") && l.Contains("fail-open"));
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
    /// Content-Type 落 Content 头，业务头透传，Origin/Referer 改写为 dsh 自源
    /// （页源代理 URL 触发网关 403，dispatch 实证）。</summary>
    [Fact]
    public void BuildForwardRequest_AppliesHeaderPolicy()
    {
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["Host"] = "page.example",
            ["Cookie"] = "page-cookie=1",
            ["Accept-Encoding"] = "gzip",
            ["Origin"] = "http://localhost:9",
            ["Referer"] = "http://localhost:9/chat",
            ["X-Probe"] = "1",
        };

        using HttpRequestMessage request = DshShellForward.BuildForwardRequest(
            "POST", "http://127.0.0.1:9/rpc", [1, 2, 3], headers, "shell-cookie=2");

        Assert.Equal("1", string.Join(",", request.Headers.GetValues("X-Probe")));
        Assert.False(request.Headers.Contains("Host"));
        Assert.False(request.Headers.Contains("Accept-Encoding"));
        Assert.Equal(["shell-cookie=2"], request.Headers.GetValues("Cookie"));
        Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
        Assert.Equal(["http://127.0.0.1:9"], request.Headers.GetValues("Origin"));
        Assert.Equal(["http://127.0.0.1:9/rpc"], request.Headers.GetValues("Referer"));
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

    /// <summary>dsh 门摹拟应答者：正确 token 303 + 铸 cookie；铸币后的会话面探活（POST /api/session/list
    /// 携带该 cookie）按就绪时延配置回 200 有体（<see cref="ReadyDelayMilliseconds"/>：-1 = 恒不就绪；
    /// 0 = 即刻）；其余 401（铸币面最小形态；转发执行面的桩见 <c>DshLoopbackProxyTests.StubDshHandler</c>）。</summary>
    private sealed class DshMimicResponder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serving;

        /// <summary>会话面就绪时延（毫秒）：0 即刻 200；&gt;0 延迟后 200；-1 恒 401（模拟会话服务永不激活）。</summary>
        public int ReadyDelayMilliseconds { get; init; }

        /// <summary>是否观察到携带铸得 cookie 的会话面探活（POST /api/session/list）。</summary>
        public bool ProbeCookieSeen { get; private set; }

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

        private async Task HandleAsync(TcpClient client, CancellationToken ct)
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

                    // POST 带体：HttpClient 的头与体可能分两次写——按 Content-Length 读满再应答（只读一次会把体落在下一写里）。
                    int total = read;
                    while (true)
                    {
                        string soFar = Encoding.ASCII.GetString(buf, 0, total);
                        int headEnd = soFar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        int contentLength = 0;
                        int bodyStart = -1;
                        if (headEnd >= 0)
                        {
                            bodyStart = headEnd + 4;
                            foreach (string headerLine in soFar[..headEnd].Split("\r\n"))
                            {
                                if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                {
                                    _ = int.TryParse(headerLine["Content-Length:".Length..].Trim(), out contentLength);
                                }
                            }

                            if (total - bodyStart >= contentLength)
                            {
                                break;
                            }
                        }

                        int more = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
                        if (more == 0)
                        {
                            break;
                        }

                        total += more;
                    }

                    string head = Encoding.ASCII.GetString(buf, 0, total);
                    string requestLine = head.Split("\r\n")[0];
                    byte[] bytes;
                    if (requestLine.StartsWith("GET /?token=" + GoodToken + " ", StringComparison.Ordinal))
                    {
                        bytes = Encoding.ASCII.GetBytes(LoopbackHttpResponder.Response(
                            "HTTP/1.1 303 See Other",
                            "",
                            $"Location: ./\r\nSet-Cookie: {CookieName}={CookieValue}; Max-Age=99; Path=/; HttpOnly; SameSite=Strict\r\n"));
                    }
                    else if (requestLine.StartsWith("POST /api/session/list ", StringComparison.Ordinal)
                        && head.Contains($"Cookie: {CookieName}={CookieValue}", StringComparison.Ordinal))
                    {
                        ProbeCookieSeen = true;
                        if (ReadyDelayMilliseconds < 0)
                        {
                            bytes = Encoding.ASCII.GetBytes(LoopbackHttpResponder.Response(
                                "HTTP/1.1 401 Unauthorized", "dsh web authentication required\n"));
                        }
                        else
                        {
                            if (ReadyDelayMilliseconds > 0)
                            {
                                await Task.Delay(ReadyDelayMilliseconds, ct);
                            }

                            bytes = Encoding.ASCII.GetBytes(LoopbackHttpResponder.Response(
                                "HTTP/1.1 200 OK", "{\"type\":\"server-response\",\"rpcId\":\"00000000-0000-0000-0000-000000000000\",\"result\":{\"ok\":true,\"value\":{\"items\":[]}}}\n"));
                        }
                    }
                    else
                    {
                        bytes = Encoding.ASCII.GetBytes(LoopbackHttpResponder.Response(
                            "HTTP/1.1 401 Unauthorized", "dsh web authentication required\n"));
                    }

                    await stream.WriteAsync(bytes, ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
                {
                    // 单连接收尾：继续服务下一条。
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
