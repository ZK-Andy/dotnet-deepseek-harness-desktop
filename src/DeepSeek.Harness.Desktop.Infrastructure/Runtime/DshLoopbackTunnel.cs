using System.Net.Sockets;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>回环代理的升级隧道面（ADR loopback-forward-proxy）：向 dsh authority 建裸 TCP
/// 重放 WS 握手并双向直泵（标准反代语义：`Origin`→dsh 自源 + 贴 cookie，页源不透传）。
/// 路由经构造注入。</summary>
internal sealed class DshLoopbackTunnel
{
    private readonly DshShellForward _forward;
    private readonly Action<string> _log;

    internal DshLoopbackTunnel(DshShellForward forward, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(log);
        _forward = forward;
        _log = log;
    }

    /// <summary>升级通道隧道（标准反代语义）：向 dsh authority 建裸 TCP，
    /// 重放握手（`Origin`→dsh 自源 + 贴 cookie + `sec-fetch-site: same-origin`，其余原样），
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
            string head = BuildUpgradeHead(req, authority, cookie);
            try
            {
                await up.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
                if (req.Body is { Length: > 0 })
                {
                    await up.WriteAsync(req.Body, ct).ConfigureAwait(false);
                }

                _log($"[shell] 代理升级隧道已建（{req.Method} {ShellProxyFraming.PagePath(req.Target)} Upgrade={upgrade}；任一端关闭即收）");
                await PumpTunnelAsync(page, up, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // 握手/泵送中断：页或上游已走，静默收尾（入口 loud 已留）。
            }
        }
    }

    /// <summary>升级握手手术（标准反代语义）：`Origin`→dsh 自源 + 贴 cookie +
    /// `sec-fetch-site: same-origin`，其余原样透传；`Host` 由 TCP 目标隐含，不伪造。</summary>
    private static string BuildUpgradeHead(ShellProxyFraming.PageRequest req, string authority, string cookie)
    {
        var head = new StringBuilder();
        head.Append(req.Method).Append(' ').Append(req.Target).Append(" HTTP/1.1\r\n");
        foreach ((string name, string value) in req.Headers)
        {
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("origin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
                || name.Equals("sec-fetch-site", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        head.Append("Origin: ").Append(authority).Append("\r\n");
        head.Append("sec-fetch-site: same-origin\r\n");
        if (!string.IsNullOrEmpty(cookie))
        {
            head.Append("Cookie: ").Append(cookie).Append("\r\n");
        }

        head.Append("\r\n");
        return head.ToString();
    }

    /// <summary>双向直泵至任一端关闭（两任务皆被观察；关闭后对端随 socket 释放中断，无计时器）。</summary>
    private static async Task PumpTunnelAsync(NetworkStream page, NetworkStream up, CancellationToken ct)
    {
        Task toUpstream = page.CopyToAsync(up, ct);
        Task toPage = up.CopyToAsync(page, ct);
        await Task.WhenAny(toUpstream, toPage).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(toUpstream, toPage).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // 对端已关：隧道使命结束。
        }
    }
}
