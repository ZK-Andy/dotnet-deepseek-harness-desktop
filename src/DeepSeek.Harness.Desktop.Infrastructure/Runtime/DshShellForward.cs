using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>壳路由家 + dsh 请求共享策略（铸币对齐上游
/// <c>packages/client/connection/src/browser-auth.ts</c> 的 <c>authorizeIndex</c>：
/// token 跳 303 铸 cookie；转发面逐请求贴 cookie、扣 <c>set-cookie</c> 不回页面）。
/// 渲染器永不见 token 与 cookie；dsh 鉴权门原样保留
/// （每个转发请求都带合法 cookie，独立运行的 dsh 行为零变化）。
/// 传输面在 <see cref="DshLoopbackProxy"/>（回环代理源，流式透传）；本类只留铸币态、
/// 路由解析与请求构造（ADR loopback-forward-proxy）。</summary>
public sealed class DshShellForward
{
    // 回环调用上限：dsh 同机响应毫秒级，30s 只防挂死（非用户可调行为，不进 RuntimeTimeouts）。
    private static readonly TimeSpan s_rpcTimeout = TimeSpan.FromSeconds(30);

    // 铸币稳定化默认参数：稳定窗/节拍与收养链探测同参（RelayWebReadinessGate 文档：节拍 1s 下
    // 2s 窗即 ≥3 拍连续 Ready）；预算封顶后 fail-open。非用户可调行为，不进 RuntimeTimeouts。
    private static readonly ReadyStabilization s_defaultStabilization = new(
        StableWindow: TimeSpan.FromSeconds(2),
        PollInterval: TimeSpan.FromSeconds(1),
        Budget: TimeSpan.FromSeconds(30));

    /// <summary>铸币稳定化参数（internal 测试缝：经构造注入覆写以压缩时长；null = 生产默认）。</summary>
    /// <param name="StableWindow">Ready 须连续维持的时长（<see cref="RelayWebReadinessGate"/>）。</param>
    /// <param name="PollInterval">探活轮询节拍。</param>
    /// <param name="Budget">稳定化总预算；到期未稳定即 fail-open 放行。</param>
    internal sealed record ReadyStabilization(TimeSpan StableWindow, TimeSpan PollInterval, TimeSpan Budget);

    // 转发体上限：与 Ryn 本地 IPC 服务同口径（32MB），超限 loud 502（不抛，页面看错误体）。
    internal const long MaxBodyBytes = 32L * 1024 * 1024;

    // 转发跟 303 上限：dsh 铸币链只一跳，多跳即异常形态，loud 502。
    internal const int MaxRedirectFollows = 3;

    // 请求头黑名单：Host/Cookie 由 HttpClient 与本类接管；Accept-Encoding 强制 identity
    // （回包不带 content-encoding，gzip 字节原样返回即页面乱码）；其余原样透传。
    internal static readonly HashSet<string> s_droppedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "cookie", "accept-encoding",
    };

    private readonly HttpClient _client;
    private readonly object _gate = new();
    private readonly ReadyStabilization? _stabilization;
    private string _authority = string.Empty;
    private string _cookieHeader = string.Empty;

    // 铸币就绪门（单调：首次铸币成功即永久就绪；holder 长轮询的等待位，无计时器，
    // 中止即页断联/应用退出，见 ADR loopback-forward-proxy）。
    private readonly TaskCompletionSource _mintedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>构造转发器（应用单例；HttpClient 生命周期随实例）。</summary>
    public DshShellForward()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
    }

    /// <summary>测试缝：注入传输（回环夹具）。</summary>
    internal DshShellForward(HttpMessageHandler handler)
        : this(handler, null)
    {
    }

    /// <summary>测试缝：注入传输与稳定化参数（回环夹具压缩时长用）。</summary>
    internal DshShellForward(HttpMessageHandler handler, ReadyStabilization? stabilization)
    {
        _client = new HttpClient(handler) { Timeout = s_rpcTimeout };
        _stabilization = stabilization;
    }

    /// <summary>用 token URL 铸币并记住 authority + cookie（覆盖式；失败 loud 返回 false，不抛）。
    /// 303 铸到 cookie 后先做就绪稳定化（cookie 探活 <c>POST /api/session/list</c>，Ready 连续维持达
    /// 稳定窗才放行；预算耗尽 fail-open），route 写入与就绪放行同时落定——holder reload 落地时
    /// dsh 会话服务必然已就绪（ADR holder-mint-gate-deepening）。</summary>
    /// <param name="url">dsh 完整端点（含 token，仅发请求，值永不记日志）。</param>
    /// <param name="log">日志回调（只记状态/头名，值永不落盘）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 铸到 cookie（含稳定化 fail-open）；false = 链断（日志已留痕，调用方按降级走）。</returns>
    public async Task<bool> MintAsync(DshWebUrl url, Action<string> log, CancellationToken ct)
    {
        string origin = url.Authority;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url.Value);
            using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            List<string> names = [];
            List<string> pairs = [];
            if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
            {
                foreach (string sc in setCookies)
                {
                    int eq = sc.IndexOf('=');
                    int semi = sc.IndexOf(';');
                    names.Add(eq > 0 ? sc[..eq] : sc);
                    pairs.Add(eq > 0 ? sc[..(semi > 0 ? semi : sc.Length)] : sc);
                }
            }

            bool minted = (int)response.StatusCode == 303 && pairs.Count > 0;
            log($"[shell] 铸币：token 跳 → {(int)response.StatusCode}（set-cookie=[{string.Join(",", names)}] 共{names.Count}个；{origin}）");
            if (!minted)
            {
                return false;
            }

            string cookie = string.Join("; ", pairs);
            await WaitWebFaceStableAsync(origin, cookie, log, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _authority = origin;
                _cookieHeader = cookie;
            }

            _mintedTcs.TrySetResult();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 异常消息可能含请求 URI（token）：用 URL 自带 token 脱敏后 loud，永不抛。
            string message = ex.Message;
            foreach (string segment in url.Value.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment.StartsWith("token=", StringComparison.Ordinal))
                {
                    message = message.Replace(segment["token=".Length..], "***", StringComparison.Ordinal);
                }
            }

            log($"[shell] 铸币失败（降级走既有路径）：{ex.GetType().Name} {message}");
            return false;
        }
    }

    /// <summary>铸币后的就绪稳定化（ADR holder-mint-gate-deepening）：303 只证认证行挂载，前端静态面
    /// 与会话服务由更晚挂载的行提供，此刻放行只会让 holder reload 落进「UI 框架在、会话树不在」的
    /// 渐进渲染。以铸得 cookie 对 origin <c>POST /api/session/list</c>（真实 wire 路径，host.log 实证）
    /// 轮询探活，Ready（200 + 响应体非空）连续维持达稳定窗即通过——门控位置钉在会话服务就绪，
    /// 首屏回到「UI + 树一次出全」（两跳时代的渲染形态）；总预算封顶，到期未稳定 fail-open
    /// （dsh 挂死由监督器/恢复面兜底，铸币不得无界等待）。取消（ct）即上抛——与铸币语义同源。</summary>
    private async Task WaitWebFaceStableAsync(string origin, string cookie, Action<string> log, CancellationToken ct)
    {
        ReadyStabilization settings = _stabilization ?? s_defaultStabilization;
        var gate = new RelayWebReadinessGate(settings.StableWindow);
        long started = Stopwatch.GetTimestamp();
        int samples = 0;
        while (true)
        {
            samples++;
            // 单次探活钉进剩余预算：半死 dsh 挂住单连接也不得把 fail-open 拖过预算（对齐
            // ProbeLoopbackWebAsync 的「实际单次等待取较小者」口径）。
            TimeSpan remaining = settings.Budget - Stopwatch.GetElapsedTime(started);
            bool ready = await ProbeSessionListReadyAsync(
                origin, cookie, ct, remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining).ConfigureAwait(false);
            if (gate.Observe(
                    ready ? RuntimeLineageProbes.LoopbackWebProbe.Ready : RuntimeLineageProbes.LoopbackWebProbe.ServingNotReady,
                    DateTimeOffset.UtcNow))
            {
                log($"[shell] 就绪稳定化：会话面 Ready 连续维持满稳定窗（探活{samples}次；{origin}）");
                return;
            }

            if (Stopwatch.GetElapsedTime(started) >= settings.Budget)
            {
                log($"[shell] 就绪稳定化预算耗尽（实际等待{Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s，探活{samples}次）——fail-open 放行，页面健康交探针/恢复面");
                return;
            }

            await Task.Delay(settings.PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>单次会话面探活：镜像 dsh 0.2.0 客户端 <c>call()</c> 的 wire 语义（dsh-client-connection：
    /// 信封 <c>{type:"client-request",rpcId,method,payload}</c> POST 到 <c>/api/&lt;method&gt;</c>，成功回包为
    /// <c>result.ok === true</c> 的 server-response）——payload <c>{}</c> 即合法（<c>SessionListRequest = {cursor?}</c>）。
    /// Ready = 200 + 回包为 <c>result.ok:true</c> 的 server-response 信封；网关校验失败/服务未激活/超时/
    /// 应答异常一律未就绪。调用方取消（ct）即上抛，探活自身超时按未就绪折算不外抛。</summary>
    private async Task<bool> ProbeSessionListReadyAsync(string origin, string cookie, CancellationToken ct, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            // 信封为固定形状（method/payload 是常量，rpcId 是 GUID——皆无转义风险），手工拼装避免为此开源生成注册面。
            string envelope =
                "{\"type\":\"client-request\",\"rpcId\":\"" + Guid.NewGuid().ToString("D") +
                "\",\"method\":\"session/list\",\"payload\":{}}";
            using HttpRequestMessage request = BuildForwardRequest(
                "POST", origin + "/api/session/list", Encoding.UTF8.GetBytes(envelope),
                new Dictionary<string, string> { ["Content-Type"] = "application/json" }, cookie);
            using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                return false;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, cts.Token).ConfigureAwait(false);
            buffer.Position = 0;
            using JsonDocument document = await JsonDocument.ParseAsync(buffer, cancellationToken: cts.Token).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out JsonElement type) && type.ValueEquals("server-response")
                && root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException
            or ObjectDisposedException or JsonException)
        {
            return false;
        }
    }

    /// <summary>取当前铸币路由（authority + cookie 名值；调用方贴 cookie、拼 target）。
    /// 未铸币返回 false（调用方 loud 502，不抛）。</summary>
    /// <param name="authority">记住的 dsh authority（无查询串，无 token）。</param>
    /// <param name="cookie">记住的 cookie 头值（名值对；值不出本方法即不落盘）。</param>
    /// <returns>true = 已铸币；false = 未铸币。</returns>
    internal bool TryGetRoute(out string authority, out string cookie)
    {
        lock (_gate)
        {
            authority = _authority;
            cookie = _cookieHeader;
        }

        return !string.IsNullOrEmpty(authority);
    }

    /// <summary>等铸币就绪（holder 长轮询用；无超时，中止即调用方收回等待）。</summary>
    /// <param name="ct">调用方取消令牌（页断联/应用退出）。</param>
    internal Task WaitMintedAsync(CancellationToken ct) => _mintedTcs.Task.WaitAsync(ct);

    /// <summary>构造外发转发请求：黑名单头剔除 + 壳 cookie 附带 + 体与 content 头保真
    /// + 源头改写（标准反代语义）。两级落头：请求头不成落 <c>Content.Headers</c>
    /// （`Content-Type` 等随体语义头；dsh JSON RPC 无类型体即拒收，ADR loopback-forward-proxy）。
    /// 成帧头归传输层。</summary>
    /// <param name="method">请求方法。</param>
    /// <param name="target">dsh 完整目标 URL（authority + 原样 path/query）。</param>
    /// <param name="body">请求体（可空）。</param>
    /// <param name="headers">页面请求头（可空；黑名单见 <see cref="s_droppedRequestHeaders"/>）。</param>
    /// <param name="cookie">附带的 cookie 头值（可空）。</param>
    /// <returns>待发送的请求（调用方负责释放与发送）。</returns>
    internal static HttpRequestMessage BuildForwardRequest(
        string method, string target, byte[]? body,
        IReadOnlyDictionary<string, string>? headers, string? cookie)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), target);
        if (body is { Length: > 0 })
        {
            request.Content = new ByteArrayContent(body);
        }

        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
            {
                if (s_droppedRequestHeaders.Contains(name)
                    || ProxyHeaderPolicy.IsPageSourceHeader(name))
                {
                    // 黑名单头（Host/Cookie/Accept-Encoding）与页源头（Origin/Referer，安全不变量
                    // 见 ProxyHeaderPolicy）一律剔除；源头由下方统一改写为 dsh 自源。
                    continue;
                }

                if (request.Headers.TryAddWithoutValidation(name, value))
                {
                    continue;
                }

                // 请求头位拒收（Content-Type 等随体语义头）则落 Content 头；
                // 仍不成（成帧头如 Content-Length 由传输层定）即跳过。
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        // 源头改写为 dsh 自源（安全不变量，形态单源 ProxyHeaderPolicy；页源代理 URL 在此无意义且触发网关 403）。
        // target 由调用方以同一路由 authority + 页面 path 拼装（DshLoopbackProxy），
        // GetLeftPart 前缀恒成立，裁出的即原样 path/query。
        string authority = new Uri(target).GetLeftPart(UriPartial.Authority);
        (string origin, string referer) = ProxyHeaderPolicy.SelfSource(authority, target[authority.Length..]);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Referer", referer);

        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return request;
    }

    /// <summary>跟进目标解析：Location（相对按 dsh authority 解）仍指 dsh authority 即返回 dsh 形跟进 URL；
    /// 外链/非法返回 null（调用方 loud 502）。纯函数，可单测。
    /// （跟进只走 dsh 形 URL：代理回环 URL HttpClient 发得出去但语义绕回，跟进目标与回页面的代理改写是两回事。）</summary>
    internal static string? ResolveFollowTarget(string location, string authority)
    {
        if (!Uri.TryCreate(location, UriKind.RelativeOrAbsolute, out Uri? parsed) || parsed is null)
        {
            return null;
        }

        Uri resolved = parsed.IsAbsoluteUri ? parsed : new Uri(new Uri(authority + "/"), parsed);
        if (!string.Equals(resolved.GetLeftPart(UriPartial.Authority), authority, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return authority + resolved.PathAndQuery;
    }
}
