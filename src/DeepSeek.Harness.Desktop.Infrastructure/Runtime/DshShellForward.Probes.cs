using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary><see cref="DshShellForward"/> 的就绪探活面（partial，ADR 尺寸健康闸 F1）：收养重验稳定化
/// 用的会话面（<c>POST /api/session/list</c>）与通道面（<c>GET /api/remote.mux</c> 升级）单次探活。
/// 判定与门控状态在 <c>DshShellForward.Gate.cs</c>；传输与请求构造面在 <c>DshShellForward.cs</c>。</summary>
public sealed partial class DshShellForward
{
    /// <summary>单次会话面探活：镜像实机 dsh 客户端 <c>call()</c> 的 wire 语义（dsh-client-connection：
    /// 信封 <c>{type:"client-request",rpcId,method,payload}</c> POST 到 <c>/api/&lt;method&gt;</c>，成功回包为
    /// <c>result.ok === true</c> 的 server-response）。payload 必须包成 <c>{args:{_request:{}}}</c>：typert 网关
    /// 强制「恰好一个 plain-object args 字段」（dsh-api-gateway <c>invokeRpc</c> 校验），session/list 的 args
    /// 描述符只收保留空请求 <c>_request</c>（<c>{cursor}</c> 被 boundary validation 拒）。形状错时网关仍回 200，
    /// 但体是 <c>ok:false</c> 的 gateway 错误信封——判据因此恒不成立、稳定化恒 fail-open
    /// （ADR mint-probe-payload-envelope 实机实证；旧版发裸 <c>payload:{}</c> 即此形态）。
    /// Ready = 200 + 回包为 <c>result.ok:true</c> 的 server-response 信封；网关校验失败/服务未激活/超时/
    /// 应答异常一律未就绪。调用方取消（ct）即上抛，探活自身超时按未就绪折算不外抛。</summary>
    private async Task<bool> ProbeSessionListReadyAsync(string origin, string cookie, CancellationToken ct, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            // 信封为固定形状（method/args 是常量，rpcId 是 GUID——皆无转义风险），手工拼装避免为此开源生成注册面。
            // payload 的 args 包装是 typert 网关硬契约（见方法 doc）；_request 为 session/list 的保留空请求。
            string envelope =
                "{\"type\":\"client-request\",\"rpcId\":\"" + Guid.NewGuid().ToString("D") +
                "\",\"method\":\"session/list\",\"payload\":{\"args\":{\"_request\":{}}}}";
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

    /// <summary>单次通道面探活：对 origin <c>GET /api/remote.mux</c> 发 WebSocket 升级（携带 cookie），
    /// 完成真实握手、读到 101 即就绪、随即弃连。**裸 TCP 单连接**（不走 ClientWebSocket：其连接池对
    /// 被掐断的升级请求有内部重试，实测一次探活派生 ~4 条内核连接——探活须是行为良好的单连接客户端，
    /// 不给半死的 dsh 添连接风暴，多客户端同时探测的场景同理）。零字节销毁（上游 appReady 前
    /// 「无路由即 destroy」形态，无 HTTP 状态可判）/401/403/超时/异常一律未就绪。调用方取消（ct）即上抛，
    /// 探活自身超时按未就绪折算不外抛。</summary>
    private async Task<bool> ProbeMuxReadyAsync(string origin, string cookie, CancellationToken ct, TimeSpan timeout)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            Uri baseUri = new(origin);
            await client.ConnectAsync(baseUri.Host, baseUri.Port, cts.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var head = new StringBuilder();
            head.Append("GET /api/remote.mux HTTP/1.1\r\n");
            head.Append("Host: ").Append(baseUri.Authority).Append("\r\n");
            head.Append("Upgrade: websocket\r\nConnection: Upgrade\r\n");
            head.Append("Sec-WebSocket-Key: ").Append(key).Append("\r\nSec-WebSocket-Version: 13\r\n");
            if (!string.IsNullOrEmpty(cookie))
            {
                head.Append("Cookie: ").Append(cookie).Append("\r\n");
            }

            head.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cts.Token).ConfigureAwait(false);
            byte[] first = new byte[16];
            int read = 0;
            while (read < first.Length)
            {
                int n = await stream.ReadAsync(first.AsMemory(read, first.Length - read), cts.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            // 状态行前缀按实际读到的长度比（单次 ReadAsync 半段到达即误判未就绪，白耗一个节拍）。
            return read >= 12 && first.AsSpan(0, 12).SequenceEqual("HTTP/1.1 101"u8);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 连接被拒/升级被拒/零字节销毁/探活超时：未就绪；清单外异常不在此吞——
            // 上抛由铸币/重验信封 loud 留痕折算。
            return false;
        }
    }
}
