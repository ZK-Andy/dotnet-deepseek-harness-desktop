using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary><see cref="DshShellForward"/> 的铸币就绪门面（partial，ADR 尺寸健康闸 F1）：
/// 铸币/收养重验、就绪稳定化复合探活（会话面 + 通道面）与 epoch 化门控状态
/// （ADR holder-mint-gate-deepening / mint-epoch-mux-gate）。传输与请求构造面在
/// <c>DshShellForward.cs</c>。</summary>
public sealed partial class DshShellForward
{
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

    private readonly object _gate = new();
    private readonly ReadyStabilization? _stabilization;
    private string _authority = string.Empty;
    private string _cookieHeader = string.Empty;

    // 铸币就绪门（epoch 化：铸币/收养重验成功即就绪；dsh 进程死亡即经 <see cref="InvalidateRoute"/>
    // 失效重武装——holder 长轮询的等待位，无计时器，中止即页断联/应用退出，见 ADR loopback-forward-proxy）。
    private TaskCompletionSource _mintedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>用 token URL 铸币并记住 authority + cookie（覆盖式；失败 loud 返回 false，不抛）。
    /// 303 铸到 cookie 后先做就绪稳定化（cookie 复合探活：会话面 <c>POST /api/session/list</c> + 通道面
    /// <c>GET /api/remote.mux</c> 升级，Ready 连续维持达稳定窗才放行；预算耗尽 fail-open），route 写入与
    /// 就绪放行同时落定——holder reload 落地时页面可用性（含树数据通道）必然已就绪
    /// （ADR holder-mint-gate-deepening / mint-epoch-mux-gate）。</summary>
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
            return await SettleAsync(origin, cookie, cookie, "铸币", log, ct).ConfigureAwait(false);
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

    /// <summary>铸币后的就绪稳定化（ADR holder-mint-gate-deepening / mint-epoch-mux-gate）：303 只证认证行挂载，
    /// 前端静态面、会话服务与远端流通道由更晚挂载的行提供，此刻放行只会让 holder reload 落进「UI 框架在、
    /// 会话树不在」的渐进渲染。以 cookie 对 origin 复合探活——<c>POST /api/session/list</c>（会话面）+
    /// <c>GET /api/remote.mux</c> WebSocket 升级（通道面；页面的树数据钉在它连上之后，且 dsh 的该升级路由
    /// 等 <c>appReady</c> 才注册、此前升级被零字节销毁——探活判据必须含它，约束细节见 ADR）。
    /// 两判据同时 Ready 连续维持达稳定窗即通过——门控位置钉在「页面可用」，首屏回到「UI + 树一次出全」。
    /// 总预算封顶，到期未稳定 fail-open（dsh 挂死由监督器/恢复面兜底，铸币不得无界等待）。
    /// 取消（ct）即上抛——与铸币语义同源。</summary>
    private async Task WaitWebFaceStableAsync(string origin, string cookie, Action<string> log, CancellationToken ct)
    {
        ReadyStabilization settings = _stabilization ?? s_defaultStabilization;
        var gate = new RelayWebReadinessGate(settings.StableWindow);
        // 时刻全部经 _timeProvider（默认 TimeProvider.System）：生产走墙钟；测试注入虚拟时钟后
        // 「稳定窗达成 vs 预算耗尽」的分叉由时钟推进决定，与探活实际耗时/机器负载解耦
        // （墙钟竞速在 CI 并行负载下两次误判 fail-open，见 ADR mint-gate-virtual-time-seam）。
        long started = _timeProvider.GetTimestamp();
        int samples = 0;
        while (true)
        {
            samples++;
            // 单次探活钉进剩余预算：半死 dsh 挂住单连接也不得把 fail-open 拖过预算（对齐
            // ProbeLoopbackWebAsync 的「实际单次等待取较小者」口径）。通道面探活前按已耗时重算
            // 剩余——会话面吃掉预算后，挂死的升级不得再借 round 起点的旧剩余把 fail-open 拖过预算。
            TimeSpan remaining = settings.Budget - _timeProvider.GetElapsedTime(started);
            bool sessionReady = await ProbeSessionListReadyAsync(
                origin, cookie, ct, remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining).ConfigureAwait(false);
            bool muxReady = false;
            if (sessionReady)
            {
                TimeSpan muxRemaining = settings.Budget - _timeProvider.GetElapsedTime(started);
                muxReady = await ProbeMuxReadyAsync(
                    origin, cookie, ct, muxRemaining < TimeSpan.Zero ? TimeSpan.Zero : muxRemaining).ConfigureAwait(false);
            }

            if (gate.Observe(
                    muxReady ? RuntimeLineageProbes.LoopbackWebProbe.Ready : RuntimeLineageProbes.LoopbackWebProbe.ServingNotReady,
                    _timeProvider.GetUtcNow()))
            {
                log($"[shell] 就绪稳定化：会话面与通道面 Ready 连续维持满稳定窗（探活{samples}次；{origin}）");
                return;
            }

            if (_timeProvider.GetElapsedTime(started) >= settings.Budget)
            {
                log($"[shell] 就绪稳定化预算耗尽（实际等待{_timeProvider.GetElapsedTime(started).TotalSeconds:F1}s，探活{samples}次）——fail-open 放行，页面健康交探针/恢复面");
                return;
            }

            await Task.Delay(settings.PollInterval, _timeProvider, ct).ConfigureAwait(false);
        }
    }

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

    /// <summary>铸币态失效（epoch 化，ADR mint-epoch-mux-gate）：dsh 进程死亡即由监督面调用——route 清空
    /// （代理 `/` 立即回落 holder、其余请求 502，页面自刷被门控吸收），就绪门重新武装（下一个未决门
    /// 由后续铸币/重验放行）。cookie 保留作收养重验的凭据材料：dsh 会话 cookie 由落盘持久密钥签名、
    /// 按 authority 绑定，同端口续任者仍有效（收养收编 AdoptSuccessor 的既有口径同源）。
    /// 幂等：已处失效态（route 空且就绪门未决）即 no-op 返回 false——监督器残留锁死分支逐轮重入不得
    /// 反复换门（旧实现逐轮替换未决 TCS 会取消 holder 已在等待的长轮询，打熄其有界重试链），
    /// 也不逐轮翻转留痕。</summary>
    /// <returns>true = 本次确有失效翻转；false = 已处失效态（无动作）。</returns>
    public bool InvalidateRoute()
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_authority) && !_mintedTcs.Task.IsCompleted)
            {
                return false;
            }

            // route 有效 ⇒ 门必已随铸币/重验放行完成（写入与置结果同临界区）：
            // 失效只把「已完成门」换成未决新门，无在等等待者可取消。
            _authority = string.Empty;
            _mintedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    /// <summary>收养重验（token-free 的 epoch 重铸，ADR mint-epoch-mux-gate）：市场接力续任者的
    /// per-process token 壳拿不到，旧 token URL 铸币恒 401；但会话 cookie 同 authority 仍有效。
    /// 以现持 cookie 对 <paramref name="url"/> origin 复合探活（会话面 + 通道面，同铸币稳定化），
    /// 稳定即重指 route 并放行（holder 长轮询解除）。无存 cookie 即回落 <see cref="MintAsync"/>
    /// （token 铸币路径：壳自 spawn 的 URL 带新 token 仍可用）。返回 false = 链断（日志已留痕，
    /// 调用方按降级走）；取消（ct）即上抛。</summary>
    public async Task<bool> RevalidateAsync(DshWebUrl url, Action<string> log, CancellationToken ct)
    {
        string origin = url.Authority;
        string cookie;
        lock (_gate)
        {
            cookie = _cookieHeader;
        }

        if (string.IsNullOrEmpty(cookie))
        {
            log("[shell] 收养重验无存 cookie，回落 token 铸币");
            return await MintAsync(url, log, ct).ConfigureAwait(false);
        }

        return await SettleAsync(origin, cookie, writeCookie: null, "收养重验", log, ct).ConfigureAwait(false);
    }

    /// <summary>就绪稳定化 + 落定（Mint/Revalidate 共用尾部）：复合探活稳定后放行就绪门。route 写入
    /// 与 TCS 置结果必须同临界区（<see cref="InvalidateRoute"/> 在锁内替换 TCS——锁外置结果与失效并发
    /// 即「无 route 放行」，holder 落进自旋或死进程页，R2 Blocker 1 收口；
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> 下锁内置结果不内联跑续延，安全）。
    /// cookie 分读写两位：probeCookie 供探活携带；writeCookie 非 null 才覆盖存 cookie（Mint 写铸得值；
    /// Revalidate 只重指 origin 不覆盖——并发 Mint 已换新 cookie 时覆盖即行为变更，R1 Suggestion 收口）。
    /// 失败 loud 返回 false 不抛（探活 URL 不含 token，无脱敏面）；ct 取消上抛。</summary>
    private async Task<bool> SettleAsync(string origin, string probeCookie, string? writeCookie, string label, Action<string> log, CancellationToken ct)
    {
        try
        {
            await WaitWebFaceStableAsync(origin, probeCookie, log, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _authority = origin;
                if (writeCookie is not null)
                {
                    _cookieHeader = writeCookie;
                }

                _mintedTcs.TrySetResult();
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"[shell] {label}失败（降级走既有路径）：{ex.GetType().Name} {ex.Message}");
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
}
