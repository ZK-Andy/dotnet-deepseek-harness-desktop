using System.Net.Sockets;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>回环代理的本地端点面（ADR loopback-forward-proxy）：就绪探针/指南页/未铸币 holder。
/// 无 dsh 依赖（未铸币可 serve）；路由/铸币态经构造注入的 <see cref="DshShellForward"/>。</summary>
internal sealed class DshLoopbackLocal
{
    private readonly DshShellForward _forward;
    private readonly Action<string> _log;
    private readonly string? _contentRoot;

    internal DshLoopbackLocal(DshShellForward forward, Action<string> log, string? contentRoot)
    {
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(log);
        _forward = forward;
        _log = log;
        _contentRoot = contentRoot;
    }

    /// <summary>代理本地端点：`/__shell_ready`（就绪探针：已铸币 200，未铸币长轮询等铸币）、
    /// `/__shell_guide/*`（磁盘引导页，GET/HEAD，越界 403/缺失 404/他法 405）、未铸币的 `/` 与
    /// `/index.html`（holder 页，轮询就绪后自 reload）。返回 true = 已处理。</summary>
    internal async Task<bool> RelayLocalAsync(NetworkStream stream, ShellProxyFraming.PageRequest req, CancellationToken ct)
    {
        string path = PagePathOnly(req.Target);
        if (path == "/__shell_ready")
        {
            if (!string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await ShellProxyFraming.WriteSmallAsync(stream, 405, "shell proxy: method not allowed", ct).ConfigureAwait(false);
                return true;
            }

            if (!_forward.TryGetRoute(out _, out _))
            {
                try
                {
                    await _forward.WaitMintedAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return true;
                }
            }

            await ShellProxyFraming.WriteJsonAsync(stream, 200, """{"ready":true}""", ct).ConfigureAwait(false);
            return true;
        }

        if (path == "/__shell_guide/" || path == "/__shell_guide"
            || path.StartsWith("/__shell_guide/", StringComparison.Ordinal))
        {
            await ServeGuideAsync(stream, req, path, ct).ConfigureAwait(false);
            return true;
        }

        if ((path == "/" || path == "/index.html")
            && string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase)
            && !_forward.TryGetRoute(out _, out _))
        {
            _log("[shell] 代理服务 holder 页（未铸币；就绪后页自 reload）");
            await ShellProxyFraming.WriteHtmlAsync(stream, 200, HolderPage, ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static string PagePathOnly(string target)
    {
        int q = target.IndexOf('?', StringComparison.Ordinal);
        return q >= 0 ? target[..q] : target;
    }

    // holder 页（英文极简；中文指南在 /__shell_guide/，词典零负担见 ADR）：
    // 就绪轮询无计时器：单次 fetch 即长轮询（服务端等铸币），200 即自 reload。
    // 自恢复无计时器（禁祈祷式加时）：失败（长轮询正常持有，不断即异常）先有界即时重 poll
    // 3 次，再只由事件驱动——`online`/可见性恢复/手动重试链；`busy` 守卫防多路并发 poll
    // 在铸币瞬间各回 200 致重复 reload。`display:none` 的重试链不进 innerText，不污染探针。
    // 首行文本即 Core.WebAuthRecovery.HolderMarker（裁决据此排除 holder，改文案必同步改常量）。
    private const string HolderPage =
        "<!doctype html>\n<html lang=\"en\">\n<head><meta charset=\"utf-8\">" +
        "<title>DeepSeek Harness Desktop</title></head>\n<body>\n" +
        "<p>" + WebAuthRecovery.HolderMarker + "…</p>\n" +
        "<p><a href=\"/__shell_guide/\">Troubleshooting</a></p>\n" +
        "<p><a id=\"retry\" href=\"/__shell_ready\" style=\"display:none\">Retry</a></p>\n" +
        "<script>\n(function () {\n" +
        "  var link = document.getElementById('retry');\n" +
        "  var busy = false, failed = 0;\n" +
        "  function showRetry() { if (link) link.style.display = ''; }\n" +
        "  function onFail() { busy = false; failed++; showRetry(); if (failed < 3) poll(); }\n" +
        "  function poll() {\n" +
        "    if (busy) return;\n" +
        "    busy = true;\n" +
        "    fetch('/__shell_ready', {cache: 'no-store'}).then(function (r) {\n" +
        "      if (r.ok) { location.reload(); return; }\n" +
        "      onFail();\n" +
        "    }).catch(onFail);\n" +
        "  }\n" +
        "  if (link) link.addEventListener('click', function (e) { e.preventDefault(); failed = 0; link.style.display = 'none'; poll(); });\n" +
        "  window.addEventListener('online', function () { failed = 0; poll(); });\n" +
        "  document.addEventListener('visibilitychange', function () { if (!document.hidden) { failed = 0; poll(); } });\n" +
        "  poll();\n" +
        "})();\n</script>\n</body>\n</html>\n";

    private async Task ServeGuideAsync(NetworkStream stream, ShellProxyFraming.PageRequest req, string path, CancellationToken ct)
    {
        if (!string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(req.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await ShellProxyFraming.WriteSmallAsync(stream, 405, "shell proxy: method not allowed", ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(_contentRoot))
        {
            _log("[shell] 代理指南面未配置静态根：502");
            await ShellProxyFraming.WriteSmallAsync(stream, 502, "shell proxy: no content root", ct).ConfigureAwait(false);
            return;
        }

        string relative = path == "/__shell_guide/" || path == "/__shell_guide"
            ? "index.html"
            : path["/__shell_guide/".Length..];
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_contentRoot, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await ShellProxyFraming.WriteSmallAsync(stream, 400, "shell proxy: bad path", ct).ConfigureAwait(false);
            return;
        }

        string root = Path.GetFullPath(_contentRoot);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(full, root, StringComparison.Ordinal))
        {
            _log($"[shell] 代理指南越界即拦：403（{ShellProxyFraming.PagePath(req.Target)}）");
            await ShellProxyFraming.WriteSmallAsync(stream, 403, "shell proxy: forbidden", ct).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(full))
        {
            await ShellProxyFraming.WriteSmallAsync(stream, 404, "shell proxy: not found", ct).ConfigureAwait(false);
            return;
        }

        bool headOnly = string.Equals(req.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
        string contentType = ShellProxyFraming.GuideMimeType(Path.GetExtension(full));
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            if (headOnly)
            {
                // HEAD 头与 GET 同值（Content-Length 为实长），无体。
                await ShellProxyFraming.WriteHeadAsync(stream, 200, contentType, bytes.LongLength, ct).ConfigureAwait(false);
                return;
            }

            await ShellProxyFraming.WriteBufferedAsync(stream, 200, contentType, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            await ShellProxyFraming.WriteSmallAsync(stream, 500, "shell proxy: read error", ct).ConfigureAwait(false);
        }
    }
}
