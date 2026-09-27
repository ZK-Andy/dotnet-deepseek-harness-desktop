using System.Net.Sockets;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>回环代理的 HTTP/1.1 成帧面（ADR loopback-forward-proxy）：页请求解析与回页响应
/// 组装。无决策（路由/转发由 <see cref="DshLoopbackProxy"/>），纯字节形态，可直测。</summary>
internal static class ShellProxyFraming
{
    // 请求头上限：Ryn 本地 IPC 服务同口径（32KB），超限 loud 502。
    private const int MaxHeadBytes = 32 * 1024;

    /// <summary>页请求解析产出（方法/源形目标/头/体；target 恒 origin-form）。</summary>
    internal readonly record struct PageRequest(string Method, string Target, Dictionary<string, string> Headers, byte[]? Body);

    /// <summary>解析判别：Ok=可中继；Eof=预连接/半开静默关；其余调用方 loud 502（见 ADR）。</summary>
    internal enum PageParseOutcome
    {
        Ok,
        Eof,
        TooLarge,
        UnsupportedEncoding,
        BadTarget,
    }

    /// <summary>解析结果（判别 + Ok 时的请求）。</summary>
    internal readonly record struct PageParseResult(PageParseOutcome Outcome, PageRequest? Request);

    internal static string PagePath(string target)
    {
        // 代理源 target 即 dsh path+query（token 永不在此，铸币走独立 URL）；防务：超长截断。
        return target.Length <= 300 ? target : target[..300] + "…";
    }

    internal static async Task<byte[]> ReadBodyAsync(NetworkStream stream, byte[] overread, int count, CancellationToken ct)
    {
        byte[] buf = new byte[count];
        int read = Math.Min(overread.Length, buf.Length);
        Array.Copy(overread, 0, buf, 0, read);
        while (read < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("页请求体中途断开");
            }

            read += n;
        }

        return buf;
    }

    internal static async Task<PageParseResult> ReadPageRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        // 分块累积请求头（逐字节读 syscall 太贵；头上限内必见 CRLFCRLF）。
        var head = new List<byte>();
        byte[] chunk = new byte[4096];
        int scanFrom = 0;
        while (true)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return new PageParseResult(PageParseOutcome.Eof, null);
            }

            if (n == 0)
            {
                return new PageParseResult(PageParseOutcome.Eof, null);
            }

            head.AddRange(chunk.AsSpan(0, n));
            if (head.Count > MaxHeadBytes + 4096)
            {
                return new PageParseResult(PageParseOutcome.TooLarge, null);
            }

            int end = IndexOfHeadEnd(head, ref scanFrom);
            if (end >= 0)
            {
                return await SplitPageRequestAsync(stream, head, end, ct).ConfigureAwait(false);
            }
        }
    }

    internal static int IndexOfHeadEnd(List<byte> head, ref int scanFrom)
    {
        int start = Math.Max(0, scanFrom - 3);
        for (int i = start; i + 3 < head.Count; i++)
        {
            if (head[i] == (byte)'\r' && head[i + 1] == (byte)'\n' && head[i + 2] == (byte)'\r' && head[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        scanFrom = head.Count;
        return -1;
    }

    internal static async Task<PageParseResult> SplitPageRequestAsync(
        NetworkStream stream, List<byte> head, int headEnd, CancellationToken ct)
    {
        // 头后多读的字节（body 首段/pipelined 下一请求）先留后用：本代理一律 close 定界，
        // 页侧无管线，overread 只可能是已到达的 body 首段。
        byte[] overread = head.GetRange(headEnd + 4, head.Count - headEnd - 4).ToArray();
        string[] lines = Encoding.ASCII.GetString(head.GetRange(0, headEnd).ToArray()).Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return new PageParseResult(PageParseOutcome.BadTarget, null);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0)
            {
                break;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        if (headers.TryGetValue("Transfer-Encoding", out string? transferEncoding)
            && !transferEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            // 分块请求体 v1 不实现（WebKit XHR 恒发 Content-Length）：显式拒，体不断章。
            return new PageParseResult(PageParseOutcome.UnsupportedEncoding, null);
        }

        byte[]? body = null;
        if (headers.TryGetValue("Content-Length", out string? clStr)
            && long.TryParse(clStr, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long contentLength)
            && contentLength > 0)
        {
            if (contentLength > DshShellForward.MaxBodyBytes)
            {
                return new PageParseResult(PageParseOutcome.TooLarge, null);
            }

            try
            {
                body = await ReadBodyAsync(stream, overread, (int)contentLength, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return new PageParseResult(PageParseOutcome.Eof, null);
            }
        }

        if (!requestLine[1].StartsWith("/", StringComparison.Ordinal))
        {
            // 非 origin-form 目标（绝对 URL/CONNECT）：本代理只做同源中继。
            return new PageParseResult(PageParseOutcome.BadTarget, null);
        }

        return new PageParseResult(PageParseOutcome.Ok, new PageRequest(requestLine[0], requestLine[1], headers, body));
    }

    internal static async Task WriteSmallAsync(NetworkStream stream, int status, string text, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>写 JSON 小体（就绪探针；`Connection: close` 定界）。</summary>
    internal static async Task WriteJsonAsync(NetworkStream stream, int status, string json, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>写 HTML 小体（holder 页；`Connection: close` 定界）。</summary>
    internal static async Task WriteHtmlAsync(NetworkStream stream, int status, string html, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        string head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>只写响应头（HEAD 语义：头与 GET 同值，无体）。</summary>
    internal static async Task WriteHeadAsync(NetworkStream stream, int status, string contentType, long contentLength, CancellationToken ct)
    {
        string head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\nContent-Length: {contentLength}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
    }

    /// <summary>指南静态 MIME（未知按 octet-stream）。</summary>
    internal static string GuideMimeType(string extension) => extension.ToUpperInvariant() switch
    {
        ".HTML" or ".HTM" => "text/html; charset=utf-8",
        ".JS" or ".MJS" => "application/javascript; charset=utf-8",
        ".CSS" => "text/css; charset=utf-8",
        ".JSON" => "application/json; charset=utf-8",
        ".SVG" => "image/svg+xml",
        ".PNG" => "image/png",
        ".ICO" => "image/x-icon",
        ".WOFF" => "font/woff",
        ".WOFF2" => "font/woff2",
        _ => "application/octet-stream",
    };

    internal static async Task WriteBufferedAsync(NetworkStream stream, int status, string contentType, byte[] bytes, CancellationToken ct)
    {
        string head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    internal static async Task WriteStreamHeadAsync(NetworkStream stream, int status, string contentType, CancellationToken ct)
    {
        // 流式回包：close 定界（分块由 dsh 侧 HttpClient 解开，此处不再组块；页侧 EventSource 照常消费）。
        string head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
    }

    internal static string Reason(int status) => status switch
    {
        200 => "OK",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        _ => "Status",
    };
}
