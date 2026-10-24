using System.Net.Sockets;

namespace DeepSeek.Harness.Desktop.Infrastructure.Runtime;

/// <summary>主实例侧的监听句柄：accept 循环读一行命令，<see cref="LauncherActivation.ShowCommand"/>
/// 则回 <see cref="LauncherActivation.AckResponse"/> 并触发显示主窗回调；其余输入静默丢弃不断链。
/// Dispose 取消循环并 unlink 锁文件；幂等，重复调用安全。生产消费仅 app 程序集（经 IIV 可达，
/// 清账批 2 收窄）。</summary>
internal sealed class PrimaryListener : IDisposable
{
    private readonly Socket _socket;
    private readonly string _path;
    private readonly Func<Task> _onShowRequested;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _acceptRetryDelay;
    private readonly TimeSpan _serveTimeout;
    private int _disposed;

    internal PrimaryListener(
        Socket socket,
        string path,
        Func<Task> onShowRequested,
        TimeSpan acceptRetryDelay,
        TimeSpan serveTimeout,
        Action<string>? log)
    {
        _socket = socket;
        _path = path;
        _onShowRequested = onShowRequested;
        _acceptRetryDelay = acceptRetryDelay;
        _serveTimeout = serveTimeout;
        _log = log;
    }

    /// <summary>启动后台 accept 循环；异常路径记日志续跑，绝不外抛拖垮宿主。</summary>
    internal void Start()
    {
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Socket client = await _socket.AcceptAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log?.Invoke($"[host] 单实例监听异常，继续：{ex.Message}");
                // 退避一拍再续：持久性故障（如 fd 耗尽）下避免热旋刷爆 host.log
                try
                {
                    await Task.Delay(_acceptRetryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // 取消竞态下 Dispose 会把挂起的 accept 转成杂散异常（ObjectDisposedException 等）：
                // 收摊阶段静默退出，不打扰日志
                return;
            }
        }
    }

    private async Task ServeAsync(Socket client)
    {
        try
        {
            using (client)
            {
                using var cts = new CancellationTokenSource(_serveTimeout);
                byte[] buffer = new byte[256];
                int read = await client.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    // 0 字节 = 连上即断：单实例探活（BindWithStaleRecovery 的 ProbeAlive）走的就是这条，
                    // 它不是请求、没有可做的事——不记「请求/忽略」行，否则每次残留自愈都往 host.log
                    // 刷两条空载荷日志，措辞还像在丢弃什么东西
                    return;
                }

                string command = System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim();
                // 空白载荷（连上只发换行）属畸形输入：冒号后给 <空> 占位，避免出现「请求忽略：」这种读不懂的半句
                string shown = command.Length == 0 ? "<空>" : command;
                // 到达性留痕：昨晚实机「launcher 唤起无响应但无任何日志」——连接到达与否
                // 必须可判（区别于「到达但处理卡死」）
                _log?.Invoke($"[host] 单实例请求：{shown}");
                if (command != LauncherActivation.ShowCommand)
                {
                    _log?.Invoke($"[host] 单实例请求忽略：{shown}");
                    return;
                }

                byte[] ack = System.Text.Encoding.UTF8.GetBytes(LauncherActivation.AckResponse);
                await client.SendAsync(ack, cts.Token).ConfigureAwait(false);
            }

            // 应答已先行发出（ack=请求受理而非执行结果）；回调 await 到完成，
            // 失败由本方法 catch 记日志——异步段异常不再逃逸
            await _onShowRequested().ConfigureAwait(false);
            _log?.Invoke("[host] 单实例请求处理完成");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[host] 单实例请求处理失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _socket.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (Exception)
        {
            // unlink 失败留给下次启动的残留自愈路径
        }

        _cts.Dispose();
    }
}
