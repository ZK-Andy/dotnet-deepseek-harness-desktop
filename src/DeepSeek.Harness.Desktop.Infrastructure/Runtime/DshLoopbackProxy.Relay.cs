using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary><see cref="DshLoopbackProxy"/> 的中继面（partial，ADR 尺寸健康闸 F1）：
/// 页请求受理/分发、转发与隧道路由、终态回写。构造/绑定序列与生命周期在
/// <c>DshLoopbackProxy.cs</c>。</summary>
public sealed partial class DshLoopbackProxy
{
    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            ShellProxyFraming.PageParseResult parsed;
            try
            {
                parsed = await ShellProxyFraming.ReadPageRequestAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 关闭期取消：静默收尾（受理读已纳入收口，不再逃成火忘 faulted task）。
                return;
            }

            if (parsed.Outcome != ShellProxyFraming.PageParseOutcome.Ok || parsed.Request is null)
            {
                if (parsed.Outcome == ShellProxyFraming.PageParseOutcome.Eof)
                {
                    // 预连接/半开连接静默关：无请求可 Serving，记 loud 即 spam。
                    return;
                }

                _log($"[shell] 代理拒畸形请求：502（{parsed.Outcome}）");
                try
                {
                    await ShellProxyFraming.WriteSmallAsync(stream, 502, "shell proxy: " + PageRefusal(parsed.Outcome), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // socket 已死：loud 已留，无 502 语义可送达。
                }

                return;
            }

            ShellProxyFraming.PageRequest req = parsed.Request.Value;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // 页断联监视只对普通请求布哨：升级连接（websocket）后续字节全是合法帧，
            // 哨兵偷字节即 corrupt 帧流又误杀隧道（Reconnecting 常亮实证）。升级隧道的存活
            // 由泵两端的 EOF/异常自然收敛，应用退出仍经 linked 走宿主取消。
            if (!IsUpgrade(req))
            {
                _ = WatchPageCloseAsync(client, linked);
            }
            try
            {
                await RelayAsync(stream, req, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 应用退出：静默收尾。
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // 页断联/dsh 不可达/超时：页 socket 已死或 502 已无意义，loud 一行即收。
                _log($"[shell] 代理中继异常收尾：{ex.GetType().Name}（{req.Method} {ShellProxyFraming.PagePath(req.Target)}）");
            }
        }
    }

    /// <summary>升级请求判定唯一家（升级隧道与 <c>RelayAsync</c> 隧道分支共用——双写收口，ADR architecture/2026-09-28-d4-cleanup-batch）：含非空 Upgrade 头即升级连接。</summary>
    private static bool IsUpgrade(ShellProxyFraming.PageRequest req) =>
        req.Headers.TryGetValue("Upgrade", out string? upgrade) && !string.IsNullOrWhiteSpace(upgrade);

    private static string PageRefusal(ShellProxyFraming.PageParseOutcome outcome) => outcome switch
    {
        ShellProxyFraming.PageParseOutcome.TooLarge => "request too large",
        ShellProxyFraming.PageParseOutcome.UnsupportedEncoding => "chunked not supported",
        ShellProxyFraming.PageParseOutcome.BadTarget => "absolute target not supported",
        _ => "bad request",
    };

    /// <summary>页断联监视：仅普通请求布哨（升级连接豁免，见分发处）。本代理一律 close 定界，
    /// 页侧不再有合法字节；读到字节/EOF/异常即判页已走，
    /// 取消在途 dsh 请求（Timeout 无限下的泄漏上界；见 ADR）。异常全吞（连接收尾即使命结束）。</summary>
    private static async Task WatchPageCloseAsync(TcpClient client, CancellationTokenSource linked)
    {
        try
        {
            byte[] one = new byte[1];
            await client.GetStream().ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // 读到字节/EOF/异常一律判页已走（close 定界下页侧无合法后字节）：下方统一取消。
        }

        try
        {
            linked.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 主流程已释放：取消已无意义。
        }
    }

    private async Task RelayAsync(NetworkStream stream, ShellProxyFraming.PageRequest req, CancellationToken ct)
    {
        // 代理本地端点（无需铸币）：就绪探针/指南页/未铸币 holder
        // （holder 长轮询等铸币，无计时器，见 ADR）。
        if (await _local.RelayLocalAsync(stream, req, ct).ConfigureAwait(false))
        {
            return;
        }

        if (IsUpgrade(req))
        {
            await _tunnel.RelayUpgradeAsync(stream, req, req.Headers["Upgrade"], ct).ConfigureAwait(false);
            return;
        }

        if (!_forward.TryGetRoute(out string authority, out string cookie))
        {
            _log($"[shell] 代理未铸币即调用：502（{req.Method} {ShellProxyFraming.PagePath(req.Target)}；dsh 未起或铸币失败）");
            await ShellProxyFraming.WriteSmallAsync(stream, 502, "shell proxy: not minted", ct).ConfigureAwait(false);
            return;
        }

        _log($"[shell] 代理请求：{req.Method} {ShellProxyFraming.PagePath(req.Target)}（头{req.Headers.Count}个，体{req.Body?.Length ?? 0}字节；cookie={(string.IsNullOrEmpty(cookie) ? "无" : "有")}）");
        HttpResponseMessage terminal = await FollowRedirectsAsync(authority, req, cookie, ct).ConfigureAwait(false);
        using (terminal)
        {
            await RelayTerminalAsync(stream, req, terminal, ct).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> FollowRedirectsAsync(
        string authority, ShellProxyFraming.PageRequest req,
        string cookie, CancellationToken ct)
    {
        string target = authority + req.Target;
        string currentMethod = req.Method;
        byte[]? currentBody = req.Body;
        for (int hop = 0; ; hop++)
        {
            HttpResponseMessage response;
            try
            {
                using HttpRequestMessage outgoing = DshShellForward.BuildForwardRequest(currentMethod, target, currentBody, req.Headers, cookie);
                response = await _client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or NotSupportedException or FormatException or ArgumentException)
            {
                return SmallResponse(502, "shell proxy: dsh unreachable");
            }

            int status = (int)response.StatusCode;
            if (status is < 300 or >= 400)
            {
                return response;
            }

            string? location = response.Headers.Location?.ToString();
            response.Dispose();
            if (hop >= DshShellForward.MaxRedirectFollows || string.IsNullOrEmpty(location))
            {
                _log($"[shell] 代理跟进终止（hop={hop}，location={(string.IsNullOrEmpty(location) ? "无" : "有")}）：502");
                return SmallResponse(502, "shell proxy: redirect not followed");
            }

            string? follow = DshShellForward.ResolveFollowTarget(location, authority);
            if (follow is null)
            {
                _log($"[shell] 代理遇外链 Location 即停（不跟进）：{authority} → 外部");
                return SmallResponse(502, "shell proxy: external redirect");
            }

            target = follow;
            currentMethod = HttpMethod.Get.Method;
            currentBody = null;
        }
    }

    private async Task RelayTerminalAsync(
        NetworkStream stream, ShellProxyFraming.PageRequest req,
        HttpResponseMessage terminal, CancellationToken ct)
    {
        // set-cookie 永不进页面（壳代持，上游同款）。
        string contentType = terminal.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        bool streamed = IsEventStream(terminal) || !terminal.Content.Headers.ContentLength.HasValue;
        if (!streamed)
        {
            byte[] bytes;
            try
            {
                bytes = await terminal.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TaskCanceledException or IOException or InvalidOperationException)
            {
                await ShellProxyFraming.WriteSmallAsync(stream, 502, "shell proxy: body unreadable", ct).ConfigureAwait(false);
                return;
            }

            if (bytes.LongLength > DshShellForward.MaxBodyBytes)
            {
                _log($"[shell] 代理回包超限：502（{req.Method} {ShellProxyFraming.PagePath(req.Target)} {bytes.LongLength}字节）");
                await ShellProxyFraming.WriteSmallAsync(stream, 502, "shell proxy: body too large", ct).ConfigureAwait(false);
                return;
            }

            _log($"[shell] 代理回包：{(int)terminal.StatusCode} {contentType} {bytes.Length}字节（{req.Method} {ShellProxyFraming.PagePath(req.Target)}）");
            await ShellProxyFraming.WriteBufferedAsync(stream, (int)terminal.StatusCode, contentType, bytes, ct).ConfigureAwait(false);
            return;
        }

        _log($"[shell] 代理流转：{(int)terminal.StatusCode} {contentType}（{req.Method} {ShellProxyFraming.PagePath(req.Target)}，页断联即停）");
        await ShellProxyFraming.WriteStreamHeadAsync(stream, (int)terminal.StatusCode, contentType, ct).ConfigureAwait(false);
        try
        {
            await terminal.Content.CopyToAsync(stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or InvalidOperationException)
        {
            // 页断联/流中断：流已尽力，无 502 语义（头已发出），静默收尾。
        }
    }

    private static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

    private static HttpResponseMessage SmallResponse(int status, string text)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes(text)),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain")
        {
            CharSet = "utf-8",
        };
        return response;
    }
}
