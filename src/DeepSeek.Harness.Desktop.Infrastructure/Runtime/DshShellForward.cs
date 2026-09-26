namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>壳 origin 常量家 + dsh 请求转发（对齐上游 <c>apps/desktop/src/web-document.ts</c>）：
/// 壳铸币、逐请求贴 cookie 转发。渲染器永不见 token 与 cookie；dsh 鉴权门原样保留
/// （每个转发请求都带合法 cookie，独立运行的 dsh 行为零变化）。</summary>
public sealed class DshShellForward
{
    /// <summary>壳 scheme 名（Ryn custom scheme 注册用；`ryn` 保留，不可用）。</summary>
    public const string ShellScheme = "dsh-app";

    /// <summary>壳 origin（窗口 URL 与 IPC/探针口径家）。</summary>
    public const string ShellOrigin = "dsh-app://app";

    /// <summary>壳根（窗口初始 URL 与导航靶点）。</summary>
    public static readonly Uri ShellRoot = new(ShellOrigin + "/", UriKind.Absolute);

    // 回环调用上限：dsh 同机响应毫秒级，30s 只防挂死（非用户可调行为，不进 RuntimeTimeouts）。
    private static readonly TimeSpan s_rpcTimeout = TimeSpan.FromSeconds(30);

    // 转发体上限：与 Ryn 本地 IPC 服务同口径（32MB），超限 loud 502（不抛，页面看错误体）。
    private const long MaxBodyBytes = 32L * 1024 * 1024;

    // 转发跟 303 上限：dsh 铸币链只一跳，多跳即异常形态，loud 502。
    private const int MaxRedirectFollows = 3;

    // 请求头黑名单：Host/Cookie 由 HttpClient 与本类接管；Accept-Encoding 强制 identity
    // （回包不带 content-encoding，gzip 字节原样返回即页面乱码）；其余原样透传。
    private static readonly HashSet<string> s_droppedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "cookie", "accept-encoding",
    };

    private readonly HttpClient _client;
    private readonly object _gate = new();
    private string _authority = string.Empty;
    private string _cookieHeader = string.Empty;

    /// <summary>构造转发器（应用单例；HttpClient 生命周期随实例）。</summary>
    public DshShellForward()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
    }

    /// <summary>测试缝：注入传输（回环夹具）。</summary>
    internal DshShellForward(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = s_rpcTimeout };
    }

    /// <summary>转发结果：状态码 + 内容类型 + 体（Location 已在内部跟完，从不外露；set-cookie 永不进页面）。</summary>
    /// <param name="Status">dsh 最终状态码。</param>
    /// <param name="ContentType">回包内容类型（含参数；取不到按 octet-stream）。</param>
    /// <param name="Body">回包体（上限 <see cref="MaxBodyBytes"/>，超限走 502 小体）。</param>
    public sealed record ForwardResult(int Status, string ContentType, byte[] Body);

    /// <summary>用 token URL 铸币并记住 authority + cookie（覆盖式；失败 loud 返回 false，不抛）。</summary>
    /// <param name="url">dsh 完整端点（含 token，仅发请求，值永不记日志）。</param>
    /// <param name="log">日志回调（只记状态/头名，值永不落盘）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 铸到 cookie；false = 链断（日志已留痕，调用方按降级走）。</returns>
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

            lock (_gate)
            {
                _authority = origin;
                _cookieHeader = string.Join("; ", pairs);
            }

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

    /// <summary>转发壳 origin 请求到 dsh：贴记住的 cookie（无则裸转，dsh 按 401 诚实回），
    /// 303 内部跟完（上限 <see cref="MaxRedirectFollows"/>，dsh 自指绝对 Location 改写回壳）。
    /// 无 ct（Ryn handler 签名无取消位；HttpClient.Timeout 兜底）。</summary>
    /// <param name="method">请求方法。</param>
    /// <param name="shellUrl">壳 URL（path/query 原样映射到记住的 dsh authority）。</param>
    /// <param name="body">请求体（可空）。</param>
    /// <param name="headers">页面请求头（可空；黑名单见 <see cref="s_droppedRequestHeaders"/>）。</param>
    /// <param name="log">日志回调（跟进/502 loud；值永不落盘）。</param>
    /// <returns>转发放回（状态/类型/体）。</returns>
    public async Task<ForwardResult> ForwardAsync(
        string method, Uri shellUrl, byte[]? body, IReadOnlyDictionary<string, string>? headers, Action<string> log)
    {
        string authority;
        string cookie;
        lock (_gate)
        {
            authority = _authority;
            cookie = _cookieHeader;
        }

        if (string.IsNullOrEmpty(authority))
        {
            // 未铸币即转发：dsh 未起或铸币失败——502 小体（现有恢复/降级面按错误处理，不抛）。
            return new ForwardResult(502, "text/plain; charset=utf-8", "shell forward: not minted"u8.ToArray());
        }

        string target = authority + shellUrl.PathAndQuery;
        string currentMethod = method;
        byte[]? currentBody = body;
        for (int hop = 0; ; hop++)
        {
            (int status, string contentType, byte[] bytes, string? location) =
                await SendOnceAsync(currentMethod, target, currentBody, headers, cookie).ConfigureAwait(false);
            if (status is < 300 or >= 400)
            {
                return new ForwardResult(status, contentType, bytes);
            }

            if (hop >= MaxRedirectFollows || string.IsNullOrEmpty(location))
            {
                // 跳数耗尽或无 Location 的裸 3xx：loud 502（页面拿无 Location 的 3xx 即停滞，不如明确失败）。
                log($"[shell] 转发跟进终止（hop={hop}，location={(string.IsNullOrEmpty(location) ? "无" : "有")}）：502");
                return new ForwardResult(502, "text/plain; charset=utf-8", "shell forward: redirect not followed"u8.ToArray());
            }

            string? follow = ResolveFollowTarget(location, authority);
            if (follow is null)
            {
                // 外链 Location：即停，不跟进、不贴 cookie（跟过去即把 dsh 会话 cookie 交给第三方）。
                log($"[shell] 转发遇外链 Location 即停（不跟进）：{authority} → 外部");
                return new ForwardResult(502, "text/plain; charset=utf-8", "shell forward: external redirect"u8.ToArray());
            }

            target = follow;
            currentMethod = HttpMethod.Get.Method;
            currentBody = null;
        }
    }

    /// <summary>跟进目标解析：Location（相对按 dsh authority 解）仍指 dsh authority 即返回 dsh 形跟进 URL；
    /// 外链/非法返回 null（调用方 loud 502）。纯函数，可单测。
    /// （跟进只走 dsh 形 URL：壳形 URL HttpClient 发不出去，跟进目标与回页面的壳改写是两回事。）</summary>
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

    private async Task<(int Status, string ContentType, byte[] Body, string? Location)> SendOnceAsync(
        string method, string target, byte[]? body, IReadOnlyDictionary<string, string>? headers, string cookie)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), target);
            if (body is { Length: > 0 })
            {
                request.Content = new ByteArrayContent(body);
            }

            if (headers is not null)
            {
                foreach ((string name, string value) in headers)
                {
                    if (s_droppedRequestHeaders.Contains(name))
                    {
                        continue;
                    }

                    // HttpClient 接管成帧头（Host/Content-Length 等）TryAdd 失败即跳过：成帧面归传输层，
                    // 非业务头丢失；安全相关头不在跳过集（真丢失会在 dispatch 实测现形）。
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            if (!string.IsNullOrEmpty(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or NotSupportedException or FormatException or ArgumentException)
        {
            // dsh 不可达/超时/方法、URL 或 scheme 非法：502 小体回页面（现有恢复屏/探针面按错误页处理，不抛）。
            return (502, "text/plain; charset=utf-8", "shell forward: dsh unreachable"u8.ToArray(), null);
        }

        using (response)
        {
            // set-cookie 永不进页面（壳代持，上游同款）；Location 由上层跟进，此处只传值。
            // 体读取与发送同处 try 内：无限流（SSE）在 HttpClient.Timeout 到时 TaskCanceled，
            // 中途断开 IOException——一律 502 小体，不抛（页面看错误体，不断流假设见 ADR）。
            string? location = response.Headers.Location?.ToString();
            byte[] bytes;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TaskCanceledException or IOException or InvalidOperationException)
            {
                return (502, "text/plain; charset=utf-8", "shell forward: body unreadable"u8.ToArray(), null);
            }

            if (bytes.LongLength > MaxBodyBytes)
            {
                return (502, "text/plain; charset=utf-8", "shell forward: body too large"u8.ToArray(), null);
            }

            // TODO(下批): 响应头最小集评估（ETag/缓存/Content-Disposition）——当前仅透 content-type，
            // 正确但无缓存与下载名；见 ADR shell-mint-and-forward。
            string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            return ((int)response.StatusCode, contentType, bytes, location);
        }
    }
}
