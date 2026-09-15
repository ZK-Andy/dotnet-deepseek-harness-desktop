using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests;

/// <summary>回环 HTTP 应答夹具：接力就绪探针要求 web 面「有声」
/// （<see cref="DeepSeek.Harness.Desktop.Infrastructure.Runtime.RuntimeLineageProbes.ProbeLoopbackWebAsync"/>），
/// 测试据此在既有回环端口上提供带响应体 / 3xx / 空体等固定应答。</summary>
internal static class LoopbackHttpResponder
{
    /// <summary>由 OS 分配一个当前空闲的回环端口（bind 一次拿号即释放）。</summary>
    /// <returns>端口号。</returns>
    public static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>构造固定 HTTP 应答报文（Content-Length 按体长算，Connection: close）。</summary>
    /// <param name="statusLine">状态行（含 HTTP 版本，如 <c>HTTP/1.1 401 Unauthorized</c>）。</param>
    /// <param name="body">响应体；空串即空体应答。</param>
    /// <param name="headers">额外头（每行以 CRLF 结尾）；无则空串。</param>
    /// <returns>完整报文。</returns>
    public static string Response(string statusLine, string body = "", string headers = "") =>
        $"{statusLine}\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\nConnection: close\r\n{headers}\r\n{body}";

    /// <summary>在指定回环端口起应答者（先同步 bind，再逐连接应答）。</summary>
    /// <param name="port">端口。</param>
    /// <param name="response">固定应答报文（见 <see cref="Response"/>）。</param>
    /// <param name="ct">取消令牌：停止应答循环。</param>
    /// <returns>监听器与应答任务；调用方取消并 <c>Stop()</c> 后 <c>await</c> 应答任务收尾。</returns>
    public static (TcpListener Listener, Task Serving) Start(int port, string response, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return (listener, ServeAsync(listener, Encoding.ASCII.GetBytes(response), ct));
    }

    /// <summary>应答循环：只回应真正发出 HTTP 请求的连接（TCP 探活连上即关读为空，跳过不算请求）。</summary>
    /// <param name="listener">已 Start 的监听器。</param>
    /// <param name="response">应答字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>应答任务（取消 / 监听器关闭即结束）。</returns>
    private static async Task ServeAsync(TcpListener listener, byte[] response, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return; // 测试收尾：取消或监听器已关闭
            }

            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    byte[] request = new byte[512];
                    if (await stream.ReadAsync(request, ct) == 0)
                    {
                        continue; // 只连不发的 TCP 探活：不是 HTTP 请求
                    }

                    await stream.WriteAsync(response, ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
                {
                    // 单连接被探针读完即关（1 字节就关）/ 收尾取消：继续服务下一条
                }
            }
        }
    }
}
