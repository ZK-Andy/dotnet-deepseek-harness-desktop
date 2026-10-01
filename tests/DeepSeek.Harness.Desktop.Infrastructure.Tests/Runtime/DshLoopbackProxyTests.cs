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

    /// <summary>铸币稳定化测试快参（ADR holder-mint-gate-deepening）：稳定窗 1ms、节拍 10ms（两三拍即稳）、
    /// 预算 250ms——桩不满足探活面时秒级 fail-open，全文件铸币点不再吃生产默认 2s 稳定窗。
    /// 节拍不宜再小：通道面探活每次被掐断的 ConnectAsync 会派生数条内核重试连接（实测 ~4×），
    /// 1ms 节拍在 250ms 预算里是数百连接的探活风暴，足以灌满桩的 accept 队列（页隧道 SYN 被顶进内核重试）。</summary>
    private static readonly DshShellForward.ReadyStabilization s_fast = new(
        StableWindow: TimeSpan.FromMilliseconds(1),
        PollInterval: TimeSpan.FromMilliseconds(10),
        Budget: TimeSpan.FromMilliseconds(250));

    /// <summary>SSE 首块渐进到达：后端流永不结束时，页侧首行仍须到达（缓冲实现恒等不到首行；
    /// dsh 插件 graph 靠此推送激活，mac 54 entries 实证）。</summary>
    [Fact]
    public async Task Proxy_Sse_StreamsProgressively()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub, s_fast);
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
        var forward = new DshShellForward(stub, s_fast);
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
        var forward = new DshShellForward(stub, s_fast);
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
        var forward = new DshShellForward(stub, s_fast);
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
        var forward = new DshShellForward(stub, s_fast);
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
        var forward = new DshShellForward(stub, s_fast);
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
        var forward = new DshShellForward(new StubDshHandler(), s_fast);
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
        var forward = new DshShellForward(stub, s_fast);
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
        var forward = new DshShellForward(stub, s_fast);
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

    /// <summary>升级隧道成功面：裸 TCP 直泵 + 握手手术（`Host`→dsh authority + `Origin`→dsh 自源 +
    /// 贴 cookie + same-origin），对标上游 `onBeforeSendHeaders`（dsh 远程通道 `remote.mux` 即此）。
    /// 桩按 dsh 判门：缺 Host 即 403（旧码在此红）。</summary>
    [Fact]
    public async Task Proxy_UpgradeTunnelsWithHeaderSurgery()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var wsStub = new StubWebSocketServer();
        var httpStub = new StubDshHandler();
        var forward = new DshShellForward(httpStub, s_fast);
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
            "GET /api/remote.mux HTTP/1.1\r\nHost: x\r\nReferer: http://localhost:12345/page\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
        await stream.WriteAsync(head, cts.Token);
        byte[] buf = new byte[12];
        int read = await stream.ReadAsync(buf.AsMemory(0, 12), cts.Token);

        Assert.StartsWith("HTTP/1.1 101", Encoding.ASCII.GetString(buf, 0, read), StringComparison.Ordinal);
        // 页源 Host/Referer 不透传，权威值由代理写死（裸 TCP 重放没有 HttpClient 的自动 Host）。
        Assert.Contains($"Host: 127.0.0.1:{wsStub.Port}", wsStub.LastHead!, StringComparison.Ordinal);
        Assert.DoesNotContain("Host: x", wsStub.LastHead!, StringComparison.Ordinal);
        Assert.Contains($"Referer: http://127.0.0.1:{wsStub.Port}/api/remote.mux", wsStub.LastHead!, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:12345", wsStub.LastHead!, StringComparison.Ordinal);
        Assert.True(await wsStub.HandshakeOkAsync(cts.Token));
        // 泵仍在写日志：断言前取快照（并发枚举 List 会 InvalidOperationException）。
        string[] surgeryLines = lines.ToArray();
        Assert.Contains(surgeryLines, l => l.Contains("升级隧道已建"));
        // 上游响应留痕：101 也记一行，「建了即收」时能直接读到上游以什么状态答的。
        Assert.Contains(surgeryLines, l => l.Contains("代理升级上游响应：HTTP/1.1 101"));
        await runCts.CancelAsync();
    }

    /// <summary>上游拒答面：dsh 回 403 时页拿到原样 403，且日志有「上游响应：HTTP/1.1 403」+ 收尾先关方=dsh——
    /// 2026-09-27 排障时该面全无留痕（只有建立/收尾两行，拒因不可见），此处钉住。</summary>
    [Fact]
    public async Task Proxy_UpgradeTunnel_LogsUpstreamReject()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var wsStub = new StubWebSocketServer(reject: true);
        var httpStub = new StubDshHandler();
        var forward = new DshShellForward(httpStub, s_fast);
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
        string responseHead = await ReadHttpHeadAsync(stream, cts.Token);

        Assert.StartsWith("HTTP/1.1 403", responseHead, StringComparison.Ordinal);
        string[] rejectLines = lines.ToArray();
        Assert.Contains(rejectLines, l => l.Contains("代理升级上游响应：HTTP/1.1 403 Forbidden"));
        // 收尾 loud：上游先关（dsh 侧）——与「建了即收」现场同形；页读到 403 早于收尾行落盘，等落盘。
        Assert.True(
            await WaitForLogAsync(lines, l => l.Contains("隧道已收") && l.Contains("先关方=dsh")),
            "应收尾留痕「先关方=dsh」");
        await runCts.CancelAsync();
    }

    /// <summary>升级隧道不被页断联监视误杀：升级后页侧帧是合法流量，哨兵不得偷字节、
    /// 不得取消隧道（Reconnecting 常亮根因：首帧即被偷 + 泵掐断，客户端循环重连）。
    /// 另断言收尾 loud：应用退出即“先关方=宿主取消”。</summary>
    [Fact]
    public async Task Proxy_UpgradeTunnel_SurvivesClientFrames()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var wsStub = new StubWebSocketServer();
        var httpStub = new StubDshHandler();
        var forward = new DshShellForward(httpStub, s_fast);
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
        string responseHead = await ReadHttpHeadAsync(stream, cts.Token);
        Assert.StartsWith("HTTP/1.1 101", responseHead, StringComparison.Ordinal);

        // 首帧 + 静置：旧代码哨兵在此偷走首字节并取消隧道（500ms 内必发）。
        // 首帧回显先排空（回显环弹回一切上行字节），再做 8K 完整性断言。
        byte[] frame = [0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58];
        await stream.WriteAsync(frame, cts.Token);
        byte[] frameEcho = await ReadExactAsync(stream, frame.Length, cts.Token);
        Assert.Equal(frame, frameEcho);
        await Task.Delay(500, cts.Token);

        // 8K 模式走回显环：字节必须一字不差回来（旧代码隧道已死，读超时即红）。
        byte[] pattern = new byte[8192];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i % 251);
        }

        await stream.WriteAsync(pattern, cts.Token);
        byte[] echo = await ReadExactAsync(stream, pattern.Length, cts.Token);
        Assert.Equal(pattern, echo);
        Assert.Contains(lines.ToArray(), l => l.Contains("升级隧道已建"));

        // 收尾 loud：应用退出即记“先关方=宿主取消”（泵仍在写日志，故取快照比对）。
        await runCts.CancelAsync();
        Assert.True(await WaitForLogAsync(lines, l => l.Contains("隧道已收")), "应收尾留痕");
        Assert.Contains(lines.ToArray(), l => l.Contains("隧道已收") && l.Contains("宿主取消"));
    }

    /// <summary>等日志行落盘（泵仍在写，故每次取快照比对）；超时返回 false，由断言侧给人读理由。</summary>
    private static async Task<bool> WaitForLogAsync(List<string> lines, Func<string, bool> match, int millis = 5000)
    {
        for (int waited = 0; waited < millis; waited += 100)
        {
            if (lines.ToArray().Any(match))
            {
                return true;
            }

            await Task.Delay(100, CancellationToken.None);
        }

        return lines.ToArray().Any(match);
    }

    /// <summary>上游接上就关、一个字节也不给（TCP 层 RST/FIN 型拒绝）：留痕须记「零字节即关」，
    /// 否则该形态又回到「已建/已收」两行盲区。</summary>
    [Fact]
    public async Task Proxy_UpgradeTunnel_LogsUpstreamSilentClose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var wsStub = new StubWebSocketServer(silentClose: true);
        var httpStub = new StubDshHandler();
        var forward = new DshShellForward(httpStub, s_fast);
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

        Assert.True(await WaitForLogAsync(lines, l => l.Contains("代理升级上游响应：零字节即关")), "应留痕「零字节即关」");
        Assert.True(
            await WaitForLogAsync(lines, l => l.Contains("隧道已收") && l.Contains("先关方=dsh")),
            "应收尾留痕「先关方=dsh」");
        await runCts.CancelAsync();
    }

    /// <summary>首块摘要直测：完整状态行取 CRLF 前首行；CRLF 未到齐标「半行」；超长/二进制只报字节数。</summary>
    /// <param name="raw">上游首块原文。</param>
    /// <param name="expected">期望摘要。</param>
    [Theory]
    [InlineData("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n\r\n", "HTTP/1.1 101 Switching Protocols")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 9\r\n\r\nforbidden", "HTTP/1.1 403 Forbidden")]
    [InlineData("HTTP/1.1 403 Forb", "HTTP/1.1 403 Forb（半行）")]
    public void TunnelStatusLine_ReadsFirstLine(string raw, string expected)
    {
        Assert.Equal(expected, DshLoopbackTunnel.StatusLineOf(Encoding.ASCII.GetBytes(raw)));
    }

    /// <summary>边界钉死：可打印闭端 0x7E / 越界 0x7F、160 与 161 分界、空首行（CRLF 起）、满 8 KiB 块。</summary>
    [Fact]
    public void TunnelStatusLine_Boundaries()
    {
        Assert.Equal("~", DshLoopbackTunnel.StatusLineOf("~\r\n"u8));
        Assert.Equal("非可打印首块（2 字节）", DshLoopbackTunnel.StatusLineOf([0x7E, 0x7F]));
        string line160 = new('A', 160);
        Assert.Equal(line160, DshLoopbackTunnel.StatusLineOf(Encoding.ASCII.GetBytes(line160 + "\r\n")));
        string line161 = new('A', 161);
        Assert.Equal(
            $"非可打印首块（{line161.Length + 2} 字节）",
            DshLoopbackTunnel.StatusLineOf(Encoding.ASCII.GetBytes(line161 + "\r\n")));
        Assert.Equal("非可打印首块（8 字节）", DshLoopbackTunnel.StatusLineOf("\r\nX: y\r\n"u8));
        Assert.Equal("非可打印首块（8192 字节）", DshLoopbackTunnel.StatusLineOf(new byte[8192]));
    }

    /// <summary>非状态行首块（二进制/超长）只报字节数，不把原始字节写进日志。</summary>
    [Fact]
    public void TunnelStatusLine_NonPrintableReportsBytesOnly()
    {
        byte[] binary = [0x00, 0x01, 0x02, 0xFF];
        Assert.Equal($"非可打印首块（{binary.Length} 字节）", DshLoopbackTunnel.StatusLineOf(binary));
        byte[] longLine = Encoding.ASCII.GetBytes(new string('A', 200));
        Assert.Equal($"非可打印首块（{longLine.Length} 字节）", DshLoopbackTunnel.StatusLineOf(longLine));
    }

    private static async Task<string> ReadHttpHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        byte[] buf = new byte[256];
        while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("eof before head end");
            }

            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
        }

        return sb.ToString();
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        byte[] buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(off, count - off), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("eof before full read");
            }

            off += n;
        }

        return buf;
    }

    /// <summary>holder 面：未铸币的 `/` 回 holder 页（含就绪轮询与指南链）；铸币后 `/` 走 dsh（桩 200）。</summary>
    [Fact]
    public async Task Proxy_Holder_ServedOnlyWhenUnminted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub, s_fast);
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
        // holder 首行即裁决排除用的标记常量（改文案必同步改常量，否则误判健康）。
        Assert.Contains(WebAuthRecovery.HolderMarker, heldBody, StringComparison.Ordinal);
        // 过渡屏与恢复页同款：--dshdt-* 回退调色板 + conic-gradient spinner（ADR holder-mint-gate-deepening）。
        Assert.Contains("--dshdt-label-primary", heldBody, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme:dark", heldBody, StringComparison.Ordinal);
        Assert.Contains("class=\"spin\"", heldBody, StringComparison.Ordinal);
        Assert.Contains("conic-gradient", heldBody, StringComparison.Ordinal);
        // 自恢复无计时器：失败只由事件驱动重试（online/可见性恢复/手动链），禁 setTimeout/setInterval。
        Assert.Contains("addEventListener('online'", heldBody, StringComparison.Ordinal);
        Assert.Contains("addEventListener('visibilitychange'", heldBody, StringComparison.Ordinal);
        Assert.Contains("getElementById('retry')", heldBody, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout", heldBody, StringComparison.Ordinal);
        Assert.DoesNotContain("setInterval", heldBody, StringComparison.Ordinal);

        Assert.True(await forward.MintAsync(
            DshWebUrl.From(new Uri($"http://127.0.0.1:9/?token={GoodToken}")), _ => { }, cts.Token));
        using HttpResponseMessage doc = await page.GetAsync(new Uri(proxy.Url, "/"), cts.Token);
        string docBody = await doc.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal("DSH-DOC", docBody);

        // epoch 失效（ADR mint-epoch-mux-gate）：dsh 死即 route 失效——`/` 回落 holder，
        // 重启窗口里页面自刷被门控吸收，不再落进指向已死进程的 502。
        forward.InvalidateRoute();
        using HttpResponseMessage heldAgain = await page.GetAsync(new Uri(proxy.Url, "/"), cts.Token);
        Assert.Contains(WebAuthRecovery.HolderMarker,
            await heldAgain.Content.ReadAsStringAsync(cts.Token), StringComparison.Ordinal);
        await runCts.CancelAsync();
    }

    /// <summary>就绪探针：未铸币长轮询（无计时器；500ms 内必不返回），铸币后即 200 ready。</summary>
    [Fact]
    public async Task Proxy_Ready_LongPollsUntilMinted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stub = new StubDshHandler();
        var forward = new DshShellForward(stub, s_fast);
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
        // 探活体镜像 0.2.0 客户端 wire 语义：client-request 信封 + 斜杠 method（host.log 实证形态）。
        Assert.Contains("\"type\":\"client-request\"", stub.LastProbeBody, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"session/list\"", stub.LastProbeBody, StringComparison.Ordinal);
        Assert.Contains("\"rpcId\":\"", stub.LastProbeBody, StringComparison.Ordinal);
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
            var forward = new DshShellForward(stub, s_fast);
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

    /// <summary>桩 WebSocket 服务端（裸 TCP）：按真实 dsh 网关判门（`Host` 必须是本 authority、
    /// `Origin` 必须是自源——缺 Host 即 403 forbidden），并断言握手手术（贴 cookie + same-origin），
    /// 过门回 101。判门保真只覆盖 `Host`/`Origin`（真实 dsh 对缺 cookie 回 401，此处与其余缺失项
    /// 一并按 403 简化——桩的契约面，不追 dsh 的分档）。双向泵体由对称 `CopyToAsync` 承担，
    /// 握手即契约面。</summary>
    private sealed class StubWebSocketServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _handshakeOk =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _reject;
        private readonly bool _silentClose;

        public int Port { get; }

        /// <summary>最后一次握手首部原文（判门/断言素材）。</summary>
        public string? LastHead { get; private set; }

        // 判门/拒答共用的 403 应答（体 9 字节，与 dsh 实测同形）。
        private static readonly byte[] s_forbidden = Encoding.ASCII.GetBytes(
            "HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Type: text/plain; charset=utf-8\r\n" +
            "Content-Length: 9\r\n\r\nforbidden");

        /// <param name="reject">true = 无条件 403（模拟 dsh 拒答，验上游响应留痕面）。</param>
        /// <param name="silentClose">true = 读完握手首部即关、一个字节都不回（零字节即关形态）。</param>
        public StubWebSocketServer(bool reject = false, bool silentClose = false)
        {
            _reject = reject;
            _silentClose = silentClose;
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
            // 循环受理（ADR mint-epoch-mux-gate）：复合门稳定化期间探活升级会先打到本桩
            // （探活无 Origin/sec-fetch-site，被下方严格判门 403，属预期）——桩必须继续
            // 接待随后的真实页连接。**按连接容错**：探活超时中途掐断会产生 per-connection
            // socket 异常，冒出循环即整体停服、页面隧道连接饿死（R2 收口实测）——per-connection
            // 异常必须吞在循环内，循环级异常（accept 失败/取消）才落外层。
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                // 并发处置：探活风暴（快节拍 × 客户端内部重连倍数）灌满串行受理的 accept 队列会把页
                // 隧道 SYN 顶进内核重试（秒级延迟）——处置必须不占 accept 节拍。连接所有权移交任务
                // （任务内持有并释放；外层不得再 using，否则派发即释放、连接秒死）。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        using (NetworkStream stream = client.GetStream())
                        {
                            await ServeOneAsync(stream, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
                    {
                        // 单连接收尾（探活掐断/页断联/桩侧关）：与本连接一起结束。
                    }
                });
            }
        }

        /// <summary>单连接处置：握手判门 + 101 回显环 / 403 / 零字节关。</summary>
        private async Task ServeOneAsync(NetworkStream stream, CancellationToken ct)
        {
            // 首部按 CRLFCRLF 收满：单次 ReadAsync 遇 TCP 分段会把完整首部误判成缺头（判门假红）。
            string head = await ReadHttpHeadAsync(stream, ct).ConfigureAwait(false);
            LastHead = head;
            if (_silentClose)
            {
                return;
            }

            if (_reject)
            {
                await stream.WriteAsync(s_forbidden, ct).ConfigureAwait(false);
                return;
            }

            bool ok = head.Contains("Host: 127.0.0.1:" + Port, StringComparison.OrdinalIgnoreCase)
                && head.Contains("Origin: http://127.0.0.1:" + Port, StringComparison.Ordinal)
                && head.Contains("Cookie: " + CookieName + "=", StringComparison.Ordinal)
                && head.Contains("sec-fetch-site: same-origin", StringComparison.OrdinalIgnoreCase)
                && head.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                // 对齐 dsh 网关实证：Host/Origin 门不过即 403 forbidden（体 9 字节）+ 关连接；
                // 代理把该响应原样泵给页面，客户端退避重连（ADR upgrade-tunnel-host-authority）。
                await stream.WriteAsync(s_forbidden, ct).ConfigureAwait(false);
                return;
            }

            _handshakeOk.TrySetResult();

            byte[] accept = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n");
            await stream.WriteAsync(accept, ct).ConfigureAwait(false);
            // 回显环：升级后页侧帧原样弹回，供“隧道存活 + 字节完整”断言；对端关闭即返。
            byte[] echo = new byte[8192];
            while (true)
            {
                int n = await stream.ReadAsync(echo.AsMemory(0, echo.Length), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    return;
                }

                await stream.WriteAsync(echo.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    /// <summary>TryCreate 结果对象（组合根值流产出，ADR compose-root-form-separation）：绑定成功产出
    /// 非空代理（http 回环 URL）与 CTS；Dispose 先 cancel 受理循环再放代理（顺序与组合根尾部一致）。
    /// 绑定失败路径（Proxy null → 降级 wwwroot）无法稳定注入端口级故障，属组合根消费方语义。
    /// 端口记忆缝注入密闭（缺省静态对会触真实 DSH_HOME）。</summary>
    [Fact]
    public void TryCreate_BindsAndDisposeCancelsToken()
    {
        var forward = new DshShellForward(new StubDshHandler(), s_fast);
        var lines = new List<string>();

        DshLoopbackProxy.ProxySetup setup =
            DshLoopbackProxy.TryCreate(forward, AppContext.BaseDirectory, lines.Add, () => null, _ => { });
        try
        {
            Assert.NotNull(setup.Proxy);
            Assert.StartsWith("http://localhost:", setup.Proxy.Url.ToString());
            Assert.False(setup.Cts.IsCancellationRequested);
            Assert.Contains(lines, l => l.Contains("代理源就绪"));
        }
        finally
        {
            setup.Dispose();
        }

        Assert.True(setup.Cts.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => setup.Cts.Token.Register(() => { }));
    }

    /// <summary>代理端口记忆·冷启动复用（ADR shell-proxy-port-persistence）：记忆端口空闲即绑原端口
    /// （页面 origin 稳定，会话恢复链闭合），值不变不写盘。</summary>
    [Fact]
    public void Proxy_PreferredPortFree_BindsItWithoutPersisting()
    {
        var forward = new DshShellForward(new StubDshHandler(), s_fast);
        var lines = new List<string>();
        int remembered = LoopbackHttpResponder.ReserveFreePort();
        int? persisted = null;

        using var proxy = new DshLoopbackProxy(forward, lines.Add, new StubDshHandler(),
            preferredPort: remembered, persistPort: p => persisted = p);

        Assert.Equal(remembered, proxy.Url.Port);
        Assert.Null(persisted);
    }

    /// <summary>代理端口记忆·TIME_WAIT 重绑（R2 定向核）：有序退出时壳侧主动关闭的页连接留 TIME_WAIT，
    /// 非 Windows 试绑记忆端口须凭 <c>SO_REUSEADDR</c> 成功（否则 60s 内快速重启假性漂移）。Windows 不设
    /// REUSEADDR（劫持语义，TIME_WAIT 边缘为已知局限），此测跳过。</summary>
    [Fact]
    public async Task Proxy_PreferredPort_TimeWaitRebind()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var forward = new DshShellForward(new StubDshHandler(), s_fast);
        var lines = new List<string>();
        int port = LoopbackHttpResponder.ReserveFreePort();

        // 服务端（壳侧）先主动关一条连接：本端在该端口留 TIME_WAIT。
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            using TcpClient serverSide = await listener.AcceptTcpClientAsync(cts.Token);
            serverSide.Dispose();
        }

        listener.Stop();

        int? persisted = null;
        using var proxy = new DshLoopbackProxy(forward, lines.Add, new StubDshHandler(),
            preferredPort: port, persistPort: p => persisted = p);

        Assert.Equal(port, proxy.Url.Port);
        Assert.Null(persisted);
    }

    /// <summary>代理端口记忆·被占漂移：记忆端口被占即 loud 降级 OS 分配，新端口立即持久化
    /// （下次起点即新端口；本次重启 origin 漂移丢一次恢复，与 dsh 端口漂移同口径）。</summary>
    [Fact]
    public void Proxy_PreferredPortOccupied_FallsBackAndRepersists()
    {
        var forward = new DshShellForward(new StubDshHandler(), s_fast);
        var lines = new List<string>();
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        int occupied = ((IPEndPoint)occupier.LocalEndpoint).Port;
        int? persisted = null;

        using var proxy = new DshLoopbackProxy(forward, lines.Add, new StubDshHandler(),
            preferredPort: occupied, persistPort: p => persisted = p);

        Assert.NotEqual(occupied, proxy.Url.Port);
        Assert.Equal(proxy.Url.Port, persisted);
        Assert.Contains(lines, l => l.Contains("已被占") && l.Contains(occupied.ToString(), StringComparison.Ordinal));
    }

    /// <summary>代理端口记忆·无记忆（首启/文件损坏读出 null）：OS 分配并立即持久化。</summary>
    [Fact]
    public void Proxy_NoMemory_AssignsAndPersists()
    {
        var forward = new DshShellForward(new StubDshHandler(), s_fast);
        var lines = new List<string>();
        int? persisted = null;

        using var proxy = new DshLoopbackProxy(forward, lines.Add, new StubDshHandler(),
            preferredPort: null, persistPort: p => persisted = p);

        Assert.True(proxy.Url.Port > 0);
        Assert.Equal(proxy.Url.Port, persisted);
    }

    /// <summary>桩 dsh（内存传输）：token 跳 303 + 铸 cookie；/events 永不结束的 SSE；
    /// /rpc 断言头并回体；/goto-ext 外链；/loop 自循环；/chat 带 set-cookie 的 200（验剥离）。</summary>
    private sealed class StubDshHandler : HttpMessageHandler
    {
        public string? LastContentType { get; private set; }

        public string? LastCookie { get; private set; }

        public string? LastOrigin { get; private set; }

        public string? LastProbeBody { get; private set; }

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

            if (path == "/api/session/list")
            {
                // 会话面探活靶点（铸币稳定化门）：200 + result.ok:true 的 server-response 信封即「会话服务就绪」形态。
                // 记录探活体：信封语义（client-request + session/list）在此钉死，防桩与被测物同错。
                LastProbeBody = request.Content is null ? string.Empty : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"type\":\"server-response\",\"rpcId\":\"00000000-0000-0000-0000-000000000000\"," +
                        "\"result\":{\"ok\":true,\"value\":{\"items\":[]}}}", Encoding.UTF8, "application/json"),
                };
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
