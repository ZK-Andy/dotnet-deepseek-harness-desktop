using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>dsh 认证链复演诊断（macOS 401 墙 M/R 二选一，用完即删）：条件回环服务模拟 dsh 门
/// （裸 / 401；正确 token 303 + 铸 cookie；凭 cookie 200；错 token 401），断言复演结论行
/// 区分 mint 与否，且 token/cookie 值永不进日志。</summary>
public class DshAuthReplayDiagTests
{
    private const string GoodToken = "GOODTOKEN123";
    private const string CookieName = "test-auth-localhost";
    private const string CookieValue = "SECRETVAL456";

    /// <summary>M 世界：token 有效 → mint=yes + followup=200；日志无秘密泄漏。</summary>
    [Fact]
    public async Task Replay_MintWorld_ReportsMintYesFollowup200WithoutSecrets()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new ConditionalResponder(port, cts.Token);
        var lines = new List<string>();
        await DshAuthReplayDiag.ReplayAsync(new Uri($"http://127.0.0.1:{port}/?token={GoodToken}"), lines.Add, cts.Token);

        Assert.Contains(lines, l => l.Contains("mint=yes"));
        Assert.Contains(lines, l => l.Contains("cookie-followup=200"));
        Assert.DoesNotContain(lines, l => l.Contains(GoodToken));
        Assert.DoesNotContain(lines, l => l.Contains(CookieValue));
    }

    /// <summary>R 世界：token 错误 → mint=no + followup=skip；日志无秘密泄漏。</summary>
    [Fact]
    public async Task Replay_RejectWorld_ReportsMintNoFollowupSkipWithoutSecrets()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = LoopbackHttpResponder.ReserveFreePort();
        using var server = new ConditionalResponder(port, cts.Token);
        var lines = new List<string>();
        await DshAuthReplayDiag.ReplayAsync(new Uri($"http://127.0.0.1:{port}/?token=WRONGTOKEN"), lines.Add, cts.Token);

        Assert.Contains(lines, l => l.Contains("mint=no"));
        Assert.Contains(lines, l => l.Contains("cookie-followup=skip"));
        Assert.DoesNotContain(lines, l => l.Contains("WRONGTOKEN"));
        Assert.DoesNotContain(lines, l => l.Contains(CookieValue));
    }

    /// <summary>取消即静默收工：已取消的令牌不记任何行、不抛。</summary>
    [Fact]
    public async Task Replay_CancelledToken_LogsNothingAndDoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var lines = new List<string>();
        await DshAuthReplayDiag.ReplayAsync(new Uri("http://127.0.0.1:9/?token=x"), lines.Add, cts.Token);

        Assert.Empty(lines);
    }

    /// <summary>token 提取与脱敏：无 query 返回 null；脱敏替换 token 子串。</summary>
    [Fact]
    public void TokenHelpers_ExtractAndRedact()
    {
        Assert.Equal("abc", DshAuthReplayDiag.TryGetToken(new Uri("http://127.0.0.1:1/?token=abc")));
        Assert.Equal("abc", DshAuthReplayDiag.TryGetToken(new Uri("http://127.0.0.1:1/?foo=1&token=abc")));
        Assert.Null(DshAuthReplayDiag.TryGetToken(new Uri("http://127.0.0.1:1/")));
        Assert.Equal("err *** end", DshAuthReplayDiag.Redact("err abc end", "abc"));
        Assert.Equal("plain", DshAuthReplayDiag.Redact("plain", null));
    }

    /// <summary>条件回环应答者：按请求行与 Cookie 头路由，模拟 dsh 认证门（含 303 铸 cookie）。</summary>
    private sealed class ConditionalResponder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serving;

        public ConditionalResponder(int port, CancellationToken ct)
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

        private static async Task HandleAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    byte[] buf = new byte[4096];
                    int read = await stream.ReadAsync(buf, ct);
                    if (read == 0)
                    {
                        return;
                    }

                    string head = Encoding.ASCII.GetString(buf, 0, read);
                    string requestLine = head.Split("\r\n")[0];
                    bool hasCookie = head.Contains($"Cookie: {CookieName}={CookieValue}", StringComparison.Ordinal);
                    string response = Route(requestLine, hasCookie);
                    byte[] bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes, ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
                {
                    // 单连接收尾：继续服务下一条。
                }
            }
        }

        private static string Route(string requestLine, bool hasCookie)
        {
            if (requestLine.StartsWith("GET /?token=" + GoodToken + " ", StringComparison.Ordinal))
            {
                return LoopbackHttpResponder.Response(
                    "HTTP/1.1 303 See Other",
                    "",
                    $"Location: ./\r\nSet-Cookie: {CookieName}={CookieValue}; Max-Age=99; Path=/; HttpOnly; SameSite=Strict\r\n");
            }

            if (requestLine.StartsWith("GET / ", StringComparison.Ordinal) && hasCookie)
            {
                return LoopbackHttpResponder.Response("HTTP/1.1 200 OK", new string('U', 100));
            }

            return LoopbackHttpResponder.Response("HTTP/1.1 401 Unauthorized", "dsh web authentication required\n");
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
