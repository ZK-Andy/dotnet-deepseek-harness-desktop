using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>回环代理源（ADR loopback-forward-proxy）：SSE 渐进到达、POST Content-Type 保真、
/// cookie 附带、303 跟进、未铸币/升级 502、值零泄漏。桩 dsh 走内存传输，页侧走回环真 socket。</summary>
public class DshLoopbackProxyTests
{
    private const string GoodToken = "PROXYTOKEN123";
    private const string CookieName = "test-proxy-auth";
    private const string CookieValue = "PROXYSECRET456";

    /// <summary>SSE 首块渐进到达：后端流永不结束时，页侧首行仍须到达（缓冲实现恒等不到首行；
    /// dsh 插件 graph 靠此推送激活，mac 54 entries 实证）。</summary>
    [Fact]
    public async Task Proxy_Sse_StreamsProgressively()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub);
        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, stub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);
        using var page = new HttpClient();

        using HttpResponseMessage response = await page.GetAsync(
            new Uri(proxy.Url, "events"), HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(
            await response.Content.ReadAsStreamAsync(cts.Token), Encoding.UTF8);
        string? first = await reader.ReadLineAsync(cts.Token);

        Assert.Equal(": connected", first);
        Assert.Contains(lines, l => l.Contains("代理流转") && l.Contains("/events"));
        await runCts.CancelAsync();
    }

    /// <summary>POST 体透传：Content-Type 保真（dsh JSON RPC 无类型体即拒收）、cookie 附带、体一致。</summary>
    [Fact]
    public async Task Proxy_Post_PreservesContentTypeAndCookie()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub);
        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, stub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);
        using var page = new HttpClient();

        using var content = new StringContent("{\"m\":1}", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await page.PostAsync(new Uri(proxy.Url, "rpc"), content, cts.Token);
        string body = await response.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"m\":1}", body);
        Assert.Equal("application/json; charset=utf-8", stub.LastContentType);
        Assert.Contains($"{CookieName}={CookieValue}", stub.LastCookie ?? string.Empty);
        Assert.Contains(lines, l => l.Contains("代理请求：POST /rpc"));
        Assert.Contains(lines, l => l.Contains("代理回包：200"));
        Assert.DoesNotContain(lines, l => l.Contains(GoodToken) || l.Contains(CookieValue));
        await runCts.CancelAsync();
    }

    /// <summary>GET 缓冲回放：path 原样映射 + 类型透传 + set-cookie 永不进页面。</summary>
    [Fact]
    public async Task Proxy_Get_ServesBufferedWithoutSetCookie()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub);
        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, stub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);
        using var page = new HttpClient();

        using HttpResponseMessage response = await page.GetAsync(new Uri(proxy.Url, "chat/?x=1"), cts.Token);
        string body = await response.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("/chat/?x=1", body, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await runCts.CancelAsync();
    }

    /// <summary>源头改写回归：页 Origin 到场（浏览器 POST 恒带）时改写为 dsh 自源，
    /// 否则网关 403（dispatch 实证；缺改写即红）。</summary>
    [Fact]
    public async Task Proxy_OriginRemappedToDshAuthority()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub);
        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, stub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);
        using var page = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(proxy.Url, "rpc"))
        {
            Content = new StringContent("{\"m\":1}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", proxy.Origin);
        using HttpResponseMessage response = await page.SendAsync(request, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://127.0.0.1:9", stub.LastOrigin);
        await runCts.CancelAsync();
    }

    /// <summary>外链 303 即停 502：跟进守卫只跟 dsh 自指，不贴 cookie 去第三方。</summary>
    [Fact]
    public async Task Proxy_ExternalRedirect_StopsWith502()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub);
        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, stub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);
        using var page = new HttpClient();

        using HttpResponseMessage response = await page.GetAsync(new Uri(proxy.Url, "goto-ext"), cts.Token);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(lines, l => l.Contains("外链"));
        await runCts.CancelAsync();
    }

    /// <summary>自循环 303 耗尽跳数即 loud 502，不把裸 3xx 交给页面。</summary>
    [Fact]
    public async Task Proxy_RedirectLoop_StopsWith502()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub);
        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, stub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);
        using var page = new HttpClient();

        using HttpResponseMessage response = await page.GetAsync(new Uri(proxy.Url, "loop"), cts.Token);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(lines, l => l.Contains("跟进终止"));
        await runCts.CancelAsync();
    }

    /// <summary>未铸币即请求：502 小体，不抛（降级面按错误页处理）。</summary>
    [Fact]
    public async Task Proxy_WithoutMint_Returns502WithoutThrowing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var forward = new DshShellForward(new StubDshHandler());
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, new StubDshHandler());
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);
        using var page = new HttpClient();

        using HttpResponseMessage response = await page.GetAsync(new Uri(proxy.Url, "chat"), cts.Token);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(lines, l => l.Contains("未铸币"));
        await runCts.CancelAsync();
    }

    /// <summary>分块请求体显式拒 502：不断章转发（成帧判别覆盖；WebKit 恒发 Content-Length）。</summary>
    [Fact]
    public async Task Proxy_ChunkedBody_Returns502()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub);
        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, stub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);

        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, proxy.Url.Port, cts.Token);
        using NetworkStream stream = socket.GetStream();
        byte[] head = Encoding.ASCII.GetBytes(
            "POST /rpc HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n");
        await stream.WriteAsync(head, cts.Token);
        byte[] buf = new byte[12];
        int read = await stream.ReadAsync(buf.AsMemory(0, 12), cts.Token);

        Assert.StartsWith("HTTP/1.1 502", Encoding.ASCII.GetString(buf, 0, read), StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("拒畸形请求"));
        await runCts.CancelAsync();
    }

    /// <summary>升级隧道上游不可达即 502（桩内存传输无 TCP 面；成功面见隧道单测）。</summary>
    [Fact]
    public async Task Proxy_UpgradeUnreachable_Returns502()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub);
        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, stub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);

        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, proxy.Url.Port, cts.Token);
        using NetworkStream stream = socket.GetStream();
        byte[] head = Encoding.ASCII.GetBytes(
            "GET /socket HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");
        await stream.WriteAsync(head, cts.Token);
        byte[] buf = new byte[12];
        int read = await stream.ReadAsync(buf.AsMemory(0, 12), cts.Token);

        Assert.StartsWith("HTTP/1.1 502", Encoding.ASCII.GetString(buf, 0, read), StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("升级上游不可达"));
        await runCts.CancelAsync();
    }

    /// <summary>升级隧道成功面：裸 TCP 直泵 + 握手手术（Origin→dsh 自源 + 贴 cookie + same-origin），
    /// 对标上游 `onBeforeSendHeaders`（dsh 远程通道 `remote.mux` 即此）。</summary>
    [Fact]
    public async Task Proxy_UpgradeTunnelsWithHeaderSurgery()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var wsStub = new StubWebSocketServer();
        var httpStub = new StubDshHandler();
        var forward = new DshShellForward(httpStub);
        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:{wsStub.Port}/?token={GoodToken}")), _ => { }, cts.Token));
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, httpStub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);

        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, proxy.Url.Port, cts.Token);
        using NetworkStream stream = socket.GetStream();
        byte[] head = Encoding.ASCII.GetBytes(
            "GET /api/remote.mux HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
        await stream.WriteAsync(head, cts.Token);
        byte[] buf = new byte[12];
        int read = await stream.ReadAsync(buf.AsMemory(0, 12), cts.Token);

        Assert.StartsWith("HTTP/1.1 101", Encoding.ASCII.GetString(buf, 0, read), StringComparison.Ordinal);
        Assert.True(await wsStub.HandshakeOkAsync(cts.Token));
        Assert.Contains(lines, l => l.Contains("升级隧道已建"));
        await runCts.CancelAsync();
    }

    /// <summary>holder 面：未铸币的 `/` 回 holder 页（含就绪轮询与指南链）；铸币后 `/` 走 dsh（桩 200）。</summary>
    [Fact]
    public async Task Proxy_Holder_ServedOnlyWhenUnminted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub);
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, stub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);
        using var page = new HttpClient();

        using HttpResponseMessage held = await page.GetAsync(new Uri(proxy.Url, "/"), cts.Token);
        string heldBody = await held.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        Assert.Contains("__shell_ready", heldBody, StringComparison.Ordinal);
        Assert.Contains("location.reload()", heldBody, StringComparison.Ordinal);

        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        using HttpResponseMessage doc = await page.GetAsync(new Uri(proxy.Url, "/"), cts.Token);
        string docBody = await doc.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal("DSH-DOC", docBody);
        await runCts.CancelAsync();
    }

    /// <summary>就绪探针：未铸币长轮询（无计时器；500ms 内必不返回），铸币后即 200 ready。</summary>
    [Fact]
    public async Task Proxy_Ready_LongPollsUntilMinted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub);
        var lines = new List<string>();
        using var proxy = new DshLoopbackProxy(forward, lines.Add, stub);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = proxy.RunAsync(runCts.Token);
        using var page = new HttpClient();

        Task<HttpResponseMessage> waiting = page.GetAsync(new Uri(proxy.Url, "__shell_ready"), cts.Token);
        await Task.Delay(500, cts.Token);
        Assert.False(waiting.IsCompleted);

        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        using HttpResponseMessage ready = await waiting;
        string body = await ready.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("\"ready\":true", body, StringComparison.Ordinal);
        await runCts.CancelAsync();
    }

    /// <summary>指南面：磁盘文件按 MIME 直出；越界 403；缺失 404；非 GET/HEAD 405。</summary>
    [Fact]
    public async Task Proxy_Guide_ServesDiskWithGuards()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string root = Path.Combine(Path.GetTempPath(), "proxy-guide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "js"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<html>GUIDE</html>", cts.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "js", "app.js"), "var x = 1;", cts.Token);
            var stub = new StubDshHandler();
            var forward = new DshShellForward(stub);
            var lines = new List<string>();
            using var proxy = new DshLoopbackProxy(forward, lines.Add, stub, root);
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            _ = proxy.RunAsync(runCts.Token);
            using var page = new HttpClient();

            using HttpResponseMessage index = await page.GetAsync(new Uri(proxy.Url, "__shell_guide/"), cts.Token);
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Equal("text/html; charset=utf-8", index.Content.Headers.ContentType?.ToString());

            using HttpResponseMessage js = await page.GetAsync(new Uri(proxy.Url, "__shell_guide/js/app.js"), cts.Token);
            Assert.Equal("application/javascript; charset=utf-8", js.Content.Headers.ContentType?.ToString());

            using HttpResponseMessage bare = await page.GetAsync(new Uri(proxy.Url, "__shell_guide"), cts.Token);
            string bareBody = await bare.Content.ReadAsStringAsync(cts.Token);
            Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
            Assert.Contains("GUIDE", bareBody, StringComparison.Ordinal);

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, new Uri(proxy.Url, "__shell_guide/"));
            using HttpResponseMessage head = await page.SendAsync(headRequest, cts.Token);
            string headBody = await head.Content.ReadAsStringAsync(cts.Token);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal("<html>GUIDE</html>".Length, head.Content.Headers.ContentLength);
            Assert.Equal(string.Empty, headBody);

            using HttpResponseMessage missing = await page.GetAsync(new Uri(proxy.Url, "__shell_guide/nope.html"), cts.Token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // 越界需裸 socket（HttpClient 会先规范化 `..`，真到不了服务端；WebKit 同）。
            using var raw = new TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, proxy.Url.Port, cts.Token);
            using NetworkStream rawStream = raw.GetStream();
            byte[] evil = Encoding.ASCII.GetBytes("GET /__shell_guide/../evil HTTP/1.1\r\nHost: x\r\n\r\n");
            await rawStream.WriteAsync(evil, cts.Token);
            byte[] evilBuf = new byte[12];
            int evilRead = await rawStream.ReadAsync(evilBuf.AsMemory(0, 12), cts.Token);
            Assert.StartsWith("HTTP/1.1 403", Encoding.ASCII.GetString(evilBuf, 0, evilRead), StringComparison.Ordinal);

            using HttpResponseMessage post = await page.PostAsync(
                new Uri(proxy.Url, "__shell_guide/"), new StringContent("x"), cts.Token);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            await runCts.CancelAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>桩 WebSocket 服务端（裸 TCP）：断言握手手术（Origin→authority + 贴 cookie +
    /// same-origin），回 101。双向泵体由对称 `CopyToAsync` 承担，握手即契约面。</summary>
    private sealed class StubWebSocketServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _handshakeOk =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Port { get; }

        public StubWebSocketServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = ServeAsync(_cts.Token);
        }

        public async Task<bool> HandshakeOkAsync(CancellationToken ct)
        {
            try
            {
                await _handshakeOk.Task.WaitAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();
                byte[] buf = new byte[4096];
                int read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                string head = Encoding.ASCII.GetString(buf, 0, read);
                bool ok = head.Contains("Origin: http://127.0.0.1:" + Port, StringComparison.Ordinal)
                    && head.Contains("Cookie: " + CookieName + "=", StringComparison.Ordinal)
                    && head.Contains("sec-fetch-site: same-origin", StringComparison.OrdinalIgnoreCase)
                    && head.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase);
                if (ok)
                {
                    _handshakeOk.TrySetResult();
                }

                byte[] accept = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n");
                await stream.WriteAsync(accept, ct).ConfigureAwait(false);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
                // 测试收尾。
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    /// <summary>桩 dsh（内存传输）：token 跳 303 + 铸 cookie；/events 永不结束的 SSE；
    /// /rpc 断言头并回体；/goto-ext 外链；/loop 自循环；/chat 带 set-cookie 的 200（验剥离）。</summary>
    private sealed class StubDshHandler : HttpMessageHandler
    {
        public string? LastContentType { get; private set; }

        public string? LastCookie { get; private set; }

        public string? LastOrigin { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = (request.RequestUri?.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0)
            {
                path = "/";
            }

            string query = request.RequestUri?.Query ?? string.Empty;
            if (request.Headers.TryGetValues("Cookie", out IEnumerable<string>? cookies))
            {
                LastCookie = string.Join(";", cookies);
            }

            if (request.Headers.TryGetValues("Origin", out IEnumerable<string>? origins))
            {
                LastOrigin = string.Join(",", origins);
            }

            if (query.Contains("token=" + GoodToken, StringComparison.Ordinal))
            {
                var mint = new HttpResponseMessage(HttpStatusCode.SeeOther);
                mint.Headers.TryAddWithoutValidation("Set-Cookie", $"{CookieName}={CookieValue}; Path=/; HttpOnly");
                mint.Headers.Location = new Uri("./", UriKind.Relative);
                return mint;
            }

            bool hasCookie = (LastCookie ?? string.Empty).Contains(CookieName, StringComparison.Ordinal);
            if (!hasCookie)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (path == "/events")
            {
                var events = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new HoldStream()),
                };
                events.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return events;
            }

            if (path == "/rpc")
            {
                // 网关源鉴权摹拟：Origin 到场但非 dsh 自源即 403（dispatch 实证；缺省 Origin 按 curl 放行）。
                if (LastOrigin is not null && !LastOrigin.Equals("http://127.0.0.1:9", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                LastContentType = request.Content?.Headers.ContentType?.ToString();
                string echo = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(echo, Encoding.UTF8, "application/json"),
                };
                return ok;
            }

            if (path == "/goto-ext")
            {
                var ext = new HttpResponseMessage(HttpStatusCode.SeeOther);
                ext.Headers.Location = new Uri("https://example.test/landing");
                return ext;
            }

            if (path == "/loop")
            {
                var loop = new HttpResponseMessage(HttpStatusCode.SeeOther);
                loop.Headers.Location = new Uri("/loop", UriKind.Relative);
                return loop;
            }

            if (path == "/chat")
            {
                var ui = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("UI for " + request.RequestUri?.PathAndQuery, Encoding.UTF8, "text/html"),
                };
                ui.Headers.TryAddWithoutValidation("Set-Cookie", $"{CookieName}=ROTATED; Path=/");
                return ui;
            }

            if (path == "/")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("DSH-DOC", Encoding.UTF8, "text/html"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        /// <summary>永不结束的 SSE 体：首块立即可读，之后无限挂起（页断联/测试结束即取消）。</summary>
        private sealed class HoldStream : System.IO.Stream
        {
            private bool _sent;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_sent)
                {
                    _sent = true;
                    byte[] first = ": connected\n\n"u8.ToArray();
                    first.CopyTo(buffer);
                    return first.Length;
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
