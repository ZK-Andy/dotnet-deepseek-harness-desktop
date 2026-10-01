using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>壳铸币与跟进目标解析（对齐上游 web-document.ts）：条件回环模拟 dsh 门
/// （正确 token 303 + 铸 cookie；错 token 401），断言铸币/零泄漏/取消语义/跟进纯函数。
/// 稳定化判据为复合面（会话面 + 通道面 mux 升级，ADR mint-epoch-mux-gate）；
/// 转发执行面在 <c>DshLoopbackProxy</c>（见 <c>DshLoopbackProxyTests</c>）。</summary>
public class DshShellForwardTests
{
    private const string GoodToken = "SHELLTOKEN123";
    private const string CookieName = "test-shell-auth";
    private const string CookieValue = "SHELLSECRET456";
    private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>测试快参：稳定窗 1ms、节拍 10ms（两三拍即稳）、预算 250ms（fail-open 路径测试秒级返回）。
    /// 节拍不宜再小：复合探活的通道面每次 ConnectAsync 在被掐断时会派生数条内核重试连接（实测 ~4×），
    /// 1ms 节拍在 250ms 预算里是数百连接的探活风暴，足以灌满桩的 accept 队列。</summary>
    private static readonly DshShellForward.ReadyStabilization s_fast = new(
        StableWindow: TimeSpan.FromMilliseconds(1),
        PollInterval: TimeSpan.FromMilliseconds(10),
        Budget: TimeSpan.FromMilliseconds(250));

    /// <summary>复合门测试宽参：预算放宽到 1s——夹具通道面 100ms 时延 × 3 拍探活在全量并行下
    /// 仍稳在预算内（判据走「稳定窗达成」而非 fail-open）。</summary>
    private static readonly DshShellForward.ReadyStabilization s_wide = new(
        StableWindow: TimeSpan.FromMilliseconds(1),
        PollInterval: TimeSpan.FromMilliseconds(10),
        Budget: TimeSpan.FromSeconds(1));

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
        Assert.Contains(lines, l => l.Contains("就绪稳定化：会话面与通道面 Ready"));
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
        // 就绪时延须给两次探活（建立 readySince + 维持稳定窗）留足裕量：桩对每次探活都计延迟，
        // 全量并行下 100ms×2 会顶穿 s_fast 的 250ms 预算而走 fail-open（实测 flaky）。
        using var server = new DshMimicResponder(port, cts.Token) { ReadyDelayMilliseconds = 30 };
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var lines = new List<string>();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        bool minted = await forward.MintAsync(url, lines.Add, cts.Token);

        Assert.True(minted);
        Assert.True(forward.TryGetRoute(out _, out _));
        Assert.Contains(lines, l => l.Contains("就绪稳定化：会话面与通道面 Ready"));
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

    /// <summary>探针 payload 形状钉死（ADR mint-probe-payload-envelope）：必须是 typert 网关强制的
    /// <c>{args:{_request:{}}}</c> 包装。夹具按实机网关语义拒裸形状，形状一漂移本测即红——旧版裸
    /// <c>payload:{}</c> 正是被网关以 200 + ok:false 回绝、令门控 100% fail-open 的形态。</summary>
    [Fact]
    public async Task Mint_ProbePayload_CarriesTypertArgsEnvelope()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        Assert.True(await forward.MintAsync(url, _ => { }, cts.Token));

        Assert.True(server.ProbeCookieSeen);
        Assert.False(server.ProbeArgumentsRejected, $"探针形状被网关拒：{server.LastProbeBody}");
        Assert.Contains(
            "\"method\":\"session/list\",\"payload\":{\"args\":{\"_request\":{}}}",
            server.LastProbeBody, StringComparison.Ordinal);
    }

    /// <summary>复合门钉通道面（ADR mint-epoch-mux-gate）：会话面先就绪、通道面 100ms 后才挂载时，
    /// route 在通道面就绪前不得可见（页面树数据钉在 mux 连上之后，提前放行即「UI 先出、树后到」）；
    /// 通道面就绪后复合判据达稳定窗正常放行（非 fail-open）。</summary>
    [Fact]
    public async Task Mint_CompositeGate_WaitsForMuxFace()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token) { MuxReadyDelayMilliseconds = 100 };
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_wide);
        var lines = new List<string>();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        Task<bool> minting = forward.MintAsync(url, lines.Add, cts.Token);
        await Task.Delay(50, cts.Token);

        Assert.False(minting.IsCompleted, "通道面未就绪时铸币不得完成");
        Assert.False(forward.TryGetRoute(out _, out _), "通道面未就绪前 route 不得可见");

        Assert.True(await minting);
        Assert.True(forward.TryGetRoute(out _, out _));
        Assert.True(server.MuxCookieSeen, "通道面探活应携带 cookie 完成 WebSocket 升级");
        Assert.Contains(lines, l => l.Contains("就绪稳定化：会话面与通道面 Ready"));
        Assert.DoesNotContain(lines, l => l.Contains("预算耗尽"));
    }

    /// <summary>通道面恒不就绪（零字节销毁形态）：预算耗尽 fail-open——仍铸币且日志留痕，
    /// 与会话面恒不就绪同 degradation 语义。</summary>
    [Fact]
    public async Task Mint_MuxNeverReady_FailOpenAfterBudget()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token) { MuxReadyDelayMilliseconds = -1 };
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var lines = new List<string>();
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        bool minted = await forward.MintAsync(url, lines.Add, cts.Token);

        Assert.True(minted);
        Assert.True(forward.TryGetRoute(out _, out _));
        Assert.True(server.MuxProbeDestroyed, "通道面探活应命中零字节销毁形态");
        Assert.Contains(lines, l => l.Contains("预算耗尽") && l.Contains("fail-open"));
    }

    /// <summary>epoch 失效（ADR mint-epoch-mux-gate）：铸币后失效翻转 route（代理 `/` 回落 holder）、
    /// 就绪门重武装（后续铸币放行）；重复失效幂等 no-op——不得翻转留痕、不得动新门上未决的等待
    /// （监督器残留锁死分支逐轮重入的形态）；未铸币首态失效同为 no-op。</summary>
    [Fact]
    public async Task InvalidateRoute_IsIdempotentAndRearmsGate()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var url = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        // 未铸币首态：失效 no-op，不翻转、不动未决等待。
        Task freshWaiter = forward.WaitMintedAsync(CancellationToken.None);
        Assert.False(freshWaiter.IsCompleted, "未铸币时长轮询应阻塞");
        Assert.False(forward.InvalidateRoute(), "未铸币首态失效应为 no-op");
        Assert.False(freshWaiter.IsCanceled);

        // 铸币 → 失效：route 翻转清空、门重武装；重复失效幂等，新门上的等待不受扰。
        Assert.True(await forward.MintAsync(url, _ => { }, cts.Token));
        Assert.True(forward.InvalidateRoute());
        Assert.False(forward.TryGetRoute(out _, out _));
        Task rearmed = forward.WaitMintedAsync(CancellationToken.None);
        Assert.False(rearmed.IsCompleted, "失效后新门应重新武装");
        Assert.False(forward.InvalidateRoute(), "重复失效应幂等 no-op");
        Assert.False(rearmed.IsCanceled, "幂等 no-op 不得取消新门上的等待");

        // 再铸币：新门放行、route 恢复。
        Assert.True(await forward.MintAsync(url, _ => { }, cts.Token));
        Assert.True(rearmed.IsCompleted);
        Assert.True(forward.TryGetRoute(out _, out _));
    }

    /// <summary>收养重验 token-free（ADR mint-epoch-mux-gate）：存 cookie 在手时不走 303 铸币
    /// （续任者 per-process token 壳拿不到，旧 token URL 恒 401），复合探活稳定即重指 route。</summary>
    [Fact]
    public async Task Revalidate_WithStoredCookie_RepointsRouteWithoutToken()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var lines = new List<string>();
        var tokenUrl = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        Assert.True(await forward.MintAsync(tokenUrl, _ => { }, cts.Token));
        Assert.Equal(1, server.TokenMintCount);
        forward.InvalidateRoute();

        var bareOrigin = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/"));
        Assert.True(await forward.RevalidateAsync(bareOrigin, lines.Add, cts.Token));

        Assert.True(forward.TryGetRoute(out _, out _));
        Assert.Equal(1, server.TokenMintCount); // 重验不得再走 token 铸币（裸 origin 无 token 可用）
        Assert.True(server.MuxCookieSeen);
        Assert.Contains(lines, l => l.Contains("就绪稳定化：会话面与通道面 Ready"));
    }

    /// <summary>收养重验无存 cookie 即回落 token 铸币（壳自 spawn 的 URL 带新 token 仍可用）。</summary>
    [Fact]
    public async Task Revalidate_NoCookie_FallsBackToTokenMint()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new DshMimicResponder(port, cts.Token);
        var forward = new DshShellForward(
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, s_fast);
        var lines = new List<string>();
        var tokenUrl = DshWebUrl.From(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"));

        Assert.True(await forward.RevalidateAsync(tokenUrl, lines.Add, cts.Token));

        Assert.True(forward.TryGetRoute(out _, out _));
        Assert.Equal(1, server.TokenMintCount);
        Assert.Contains(lines, l => l.Contains("收养重验无存 cookie"));
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
    /// 携带该 cookie）先过实机网关形状闸——payload 未含 <c>{"args":{"_request":{}}}</c> 即回
    /// <c>200</c> + gateway 错误信封（<c>ok:false</c>；形状漂移不随时延），通过后才按
    /// <see cref="ReadyDelayMilliseconds"/> 回 200 就绪体（-1 = 恒 401 不就绪；0 = 即刻）；通道面
    /// （GET /api/remote.mux 升级，ADR mint-epoch-mux-gate）按 <see cref="MuxReadyDelayMilliseconds"/>
    /// 完成 101 握手（-1 = 零字节销毁即实机 appReady 前形态；无 cookie 401）；其余 401
    /// （铸币面最小形态；转发执行面的桩见 <c>DshLoopbackProxyTests.StubDshHandler</c>）。</summary>
    private sealed class DshMimicResponder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serving;

        /// <summary>会话面就绪时延（毫秒）：0 即刻 200；&gt;0 延迟后 200；-1 恒 401（模拟会话服务永不激活）。</summary>
        public int ReadyDelayMilliseconds { get; init; }

        /// <summary>通道面就绪时延（毫秒）：0 即刻 101；&gt;0 延迟后 101；-1 零字节销毁（模拟 appReady 前无升级路由）。</summary>
        public int MuxReadyDelayMilliseconds { get; init; }

        /// <summary>是否观察到携带铸得 cookie 的会话面探活（POST /api/session/list）。</summary>
        public bool ProbeCookieSeen { get; private set; }

        /// <summary>最近一次探活请求体（形状断言用）。</summary>
        public string LastProbeBody { get; private set; } = string.Empty;

        /// <summary>是否发生「payload 未包 args」的形状拒绝（实机网关语义：200 + ok:false 错误信封）。</summary>
        public bool ProbeArgumentsRejected { get; private set; }

        /// <summary>是否观察到携带 cookie 的通道面升级（101 握手完成）。</summary>
        public bool MuxCookieSeen { get; private set; }

        /// <summary>是否观察到零字节销毁形态的通道面升级。</summary>
        public bool MuxProbeDestroyed { get; private set; }

        /// <summary>已发出的 303 铸币次数（重验 token-free 断言用）。</summary>
        public int TokenMintCount { get; private set; }

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
                    int bodyHeadEnd = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    string body = bodyHeadEnd >= 0 && bodyHeadEnd + 4 <= head.Length ? head[(bodyHeadEnd + 4)..] : string.Empty;
                    byte[] bytes;
                    if (requestLine.StartsWith("GET /?token=" + GoodToken + " ", StringComparison.Ordinal))
                    {
                        TokenMintCount++;
                        bytes = Encoding.ASCII.GetBytes(LoopbackHttpResponder.Response(
                            "HTTP/1.1 303 See Other",
                            "",
                            $"Location: ./\r\nSet-Cookie: {CookieName}={CookieValue}; Max-Age=99; Path=/; HttpOnly; SameSite=Strict\r\n"));
                    }
                    else if (requestLine.StartsWith("GET /api/remote.mux ", StringComparison.Ordinal))
                    {
                        if (!head.Contains($"Cookie: {CookieName}={CookieValue}", StringComparison.Ordinal))
                        {
                            // 实机 rejectRemoteStreamUpgrade 语义：未认证升级回 401（带字节）。
                            bytes = Encoding.ASCII.GetBytes(LoopbackHttpResponder.Response(
                                "HTTP/1.1 401 Unauthorized", "unauthorized\n"));
                        }
                        else if (MuxReadyDelayMilliseconds < 0)
                        {
                            // 实机 appReady 前形态（ADR mint-epoch-mux-gate 根因）：无升级路由即零字节销毁。
                            MuxProbeDestroyed = true;
                            return;
                        }
                        else
                        {
                            if (MuxReadyDelayMilliseconds > 0)
                            {
                                await Task.Delay(MuxReadyDelayMilliseconds, ct);
                            }

                            MuxCookieSeen = true;
                            string key = head.Split("\r\n")
                                .First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[
                                    "Sec-WebSocket-Key:".Length..].Trim();
                            string accept = Convert.ToBase64String(
                                SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketMagic)));
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                                + $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), ct);
                            return;
                        }
                    }
                    else if (requestLine.StartsWith("POST /api/session/list ", StringComparison.Ordinal)
                        && head.Contains($"Cookie: {CookieName}={CookieValue}", StringComparison.Ordinal))
                    {
                        ProbeCookieSeen = true;
                        LastProbeBody = body;
                        if (!body.Contains("\"payload\":{\"args\":{\"_request\":{}}}", StringComparison.Ordinal))
                        {
                            // 实机网关语义（ADR mint-probe-payload-envelope 实证）：payload 未包 args 仍回 200，
                            // 但体是 ok:false 的 gateway 错误信封——探针判据因此恒不成立。
                            ProbeArgumentsRejected = true;
                            bytes = Encoding.ASCII.GetBytes(LoopbackHttpResponder.Response(
                                "HTTP/1.1 200 OK",
                                "{\"type\":\"server-response\",\"rpcId\":\"00000000-0000-0000-0000-000000000000\",\"result\":{\"ok\":false,\"error\":{\"code\":\"gateway/internal\",\"message\":\"Remote payload must contain exactly one plain-object args field\",\"details\":{}}}}\n"));
                        }
                        else if (ReadyDelayMilliseconds < 0)
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
