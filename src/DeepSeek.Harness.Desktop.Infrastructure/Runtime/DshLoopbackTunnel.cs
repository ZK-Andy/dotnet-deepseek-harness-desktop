using System.Net.Sockets;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>回环代理的升级隧道面（ADR loopback-forward-proxy）：向 dsh authority 建裸 TCP
/// 重放 WS 握手并双向直泵（标准反代语义：`Host`→dsh authority、`Origin`/`Referer`→dsh 自源 + 贴 cookie，页源不透传）。
/// 路由经构造注入。</summary>
internal sealed class DshLoopbackTunnel
{
    // 上游首块探读上限：状态行与其后的握手头都在一两个读里到齐；超出部分照常直泵。
    private const int UpstreamFirstBlockBytes = 8192;

    // 状态行留痕上限：超过即当非状态行处理（只报字节数，不把长块/二进制写进日志）。
    private const int MaxStatusLineBytes = 160;

    private readonly DshShellForward _forward;
    private readonly Action<string> _log;
    private readonly ProxyLogging _logging;

    internal DshLoopbackTunnel(DshShellForward forward, Action<string> log, ProxyLogging logging)
    {
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(logging);
        _forward = forward;
        _log = log;
        _logging = logging;
    }

    /// <summary>升级通道隧道（标准反代语义）：向 dsh authority 建裸 TCP，
    /// 重放握手（`Host`→dsh authority + `Origin`→dsh 自源 + 贴 cookie + `sec-fetch-site: same-origin`，其余原样），
    /// 而后双向直泵至任一端关闭（寿命与连接绑定，无计时器）。dsh 客户端远程通道（`remote.mux`）即此。</summary>
    internal async Task RelayUpgradeAsync(NetworkStream page, ShellProxyFraming.PageRequest req, string upgrade, CancellationToken ct)
    {
        if (!_forward.TryGetRoute(out string authority, out string cookie))
        {
            _log($"[shell] 代理升级无路由：502（{req.Method} {ShellProxyFraming.PagePath(req.Target)} Upgrade={upgrade}）");
            await ShellProxyFraming.WriteSmallAsync(page, 502, "shell proxy: not minted", ct).ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate(authority, UriKind.Absolute, out Uri? baseUri) || baseUri.Port <= 0)
        {
            _log("[shell] 代理升级路由非法：502");
            await ShellProxyFraming.WriteSmallAsync(page, 502, "shell proxy: bad route", ct).ConfigureAwait(false);
            return;
        }

        TcpClient upstream = new();
        try
        {
            await upstream.ConnectAsync(baseUri.Host, baseUri.Port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            _log($"[shell] 代理升级上游不可达：502（{ex.GetType().Name}）");
            await ShellProxyFraming.WriteSmallAsync(page, 502, "shell proxy: upstream unreachable", ct).ConfigureAwait(false);
            return;
        }

        using (upstream)
        {
            NetworkStream up = upstream.GetStream();
            string head = BuildUpgradeHead(req, baseUri, cookie);
            try
            {
                await up.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
                if (req.Body is { Length: > 0 })
                {
                    await up.WriteAsync(req.Body, ct).ConfigureAwait(false);
                }

                string label = $"{req.Method} {ShellProxyFraming.PagePath(req.Target)} Upgrade={upgrade}";
                if (_logging.Trace)
                {
                    // 逐隧道 trace 默认关（ADR proxy-log-noise-reduction）：活连接的建行是页面 mux 重连 churn 的
                    // 流水，只在 Trace 打开时落盘；异常面（无路由/不可达/零字节/上游响应/故障收尾）照旧恒打。
                    _log($"[shell] 代理升级隧道已建（{label}；任一端关闭即收）");
                }

                await PumpTunnelAsync(page, up, label, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // 握手/泵送中断：页或上游已走，静默收尾（入口 loud 已留）。
            }
        }
    }

    /// <summary>升级握手手术（标准反代语义）：`Host`→dsh authority + `Origin`/`Referer`→dsh 自源 +
    /// 贴 cookie + `sec-fetch-site: same-origin`，其余原样透传。`Host` 必写：dsh 网关先判 Host/Origin 门，
    /// 缺 Host 即 403（`forbidden`）——裸 TCP 重放没有 `HttpClient` 那种按 URI 自动补 Host 的默认。
    /// 源头改写与普通转发面同法：形态单源 <see cref="ProxyHeaderPolicy"/>（该头不在 dsh 门上，
    /// 改写只为页源值零透传这条不变量）。</summary>
    internal static string BuildUpgradeHead(ShellProxyFraming.PageRequest req, Uri baseUri, string cookie)
    {
        var head = new StringBuilder();
        head.Append(req.Method).Append(' ').Append(req.Target).Append(" HTTP/1.1\r\n");
        foreach ((string name, string value) in req.Headers)
        {
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase)
                || ProxyHeaderPolicy.IsPageSourceHeader(name)
                || name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
                || name.Equals("sec-fetch-site", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        (string origin, string referer) = ProxyHeaderPolicy.SelfSource(
            baseUri.GetLeftPart(UriPartial.Authority), req.Target);
        head.Append("Host: ").Append(baseUri.Authority).Append("\r\n");
        head.Append("Origin: ").Append(origin).Append("\r\n");
        head.Append("Referer: ").Append(referer).Append("\r\n");
        head.Append("sec-fetch-site: same-origin\r\n");
        if (!string.IsNullOrEmpty(cookie))
        {
            head.Append("Cookie: ").Append(cookie).Append("\r\n");
        }

        head.Append("\r\n");
        return head.ToString();
    }

    /// <summary>双向直泵至任一端关闭（两任务皆被观察；关闭后对端随 socket 释放中断，无计时器）。
    /// 收尾一行：先结束的方向即先关方（页/dsh/宿主取消），排障不再靠数建立行猜
    /// （Reconnecting 常亮实证：建立行刷屏、关闭零行）。取消与 EOF 同判据，先关方启发式。</summary>
    private async Task PumpTunnelAsync(NetworkStream page, NetworkStream up, string label, CancellationToken ct)
    {
        Task toUpstream = page.CopyToAsync(up, ct);
        Task toPage = RelayUpstreamAsync(up, page, label, ct);
        Task first = await Task.WhenAny(toUpstream, toPage).ConfigureAwait(false);
        string closer = ct.IsCancellationRequested ? "宿主取消"
            : ReferenceEquals(first, toUpstream) ? "页" : "dsh";
        string fault = first.Exception?.InnerExceptions.Count > 0
            ? $"（{first.Exception.InnerExceptions[0].GetType().Name}）"
            : string.Empty;
        if (ShouldLogTunnelClose(_logging, first))
        {
            _log($"[shell] 代理升级隧道已收（先关方={closer}{fault}；{label}）");
        }
        try
        {
            await Task.WhenAll(toUpstream, toPage).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // 对端已关：隧道使命结束（收行是否留痕已由 ShouldLogTunnelClose 判定）。
        }
    }

    /// <summary>收行是否落盘（ADR proxy-log-noise-reduction）：`Trace` 档恒落；默认档只在「先结束的任务以异常
    /// 收场」时落（RST 即此形态）——实机 639 条收行里恰 1 条此种（2026-09-27 mux churn 排障现场，首读即断、
    /// `已建` 与其后无 `上游响应`，全档静默即该类故障零留痕）。正常 EOF 收场与取消收场（`Task.Exception` 为 null）
    /// 不落；先结束的是正常 EOF 侧、另一侧随后才故障的情形不在本行（由泵收尾的 catch 静默）。</summary>
    /// <param name="logging">日志档位。</param>
    /// <param name="first">先结束的泵任务。</param>
    internal static bool ShouldLogTunnelClose(ProxyLogging logging, Task first) =>
        logging.Trace || first.Exception is { InnerExceptions.Count: > 0 };

    /// <summary>上游→页方向：首块解出响应首行 loud 留痕后**原样前送**，其余字节透明直泵——
    /// 「建了即收」这类故障从此能直接读到是谁、以什么状态拒的（升级面此前只有建立/收尾两行）。
    /// 无计时器：首读的阻塞语义与 <c>CopyToAsync</c> 首读等同（上游不发即等到任一端关闭）。</summary>
    private async Task RelayUpstreamAsync(NetworkStream up, NetworkStream page, string label, CancellationToken ct)
    {
        byte[] first = new byte[UpstreamFirstBlockBytes];
        int n = await up.ReadAsync(first.AsMemory(0, first.Length), ct).ConfigureAwait(false);
        if (n == 0)
        {
            // TCP 层直接关、不给应答字节：这也是「建了即收」的一种形态，同样要能读到。
            _log($"[shell] 代理升级上游响应：零字节即关（{label}）");
            return;
        }

        _log($"[shell] 代理升级上游响应：{StatusLineOf(first.AsSpan(0, n))}（{label}）");
        await page.WriteAsync(first.AsMemory(0, n), ct).ConfigureAwait(false);
        await up.CopyToAsync(page, ct).ConfigureAwait(false);
    }

    /// <summary>首块的可读摘要：CRLF 前的首行（≤<see cref="MaxStatusLineBytes"/> 字节且全可打印 ASCII）；
    /// 首块内找不到 CRLF 即标注「半行」（首读被 TCP 切分或非 CRLF 行尾，不冒充完整状态行）；
    /// 非可打印（可能是二进制上游）只报字节数，不把原始字节写进日志。</summary>
    internal static string StatusLineOf(ReadOnlySpan<byte> first)
    {
        int end = first.IndexOf("\r\n"u8);
        ReadOnlySpan<byte> line = end >= 0 ? first[..end] : first;
        if (line.Length == 0 || line.Length > MaxStatusLineBytes
            || line.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E))
        {
            return $"非可打印首块（{first.Length} 字节）";
        }

        return end >= 0 ? Encoding.ASCII.GetString(line) : Encoding.ASCII.GetString(line) + "（半行）";
    }
}
