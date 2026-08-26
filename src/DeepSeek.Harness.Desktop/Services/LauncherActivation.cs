using System.Net.Sockets;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 单实例仲裁（ADR single-instance-launcher-activation）：首实例对 Unix domain socket
/// <c>bind</code> 成功即持锁并监听；二次启动 bind 撞地址即向既有 socket 发送
/// <c>show</c> 命令请首实例显示主窗，随后自身退出。GTK/saucer 按 ApplicationId 的互斥
/// 在 Wayland 会话不生效（v0.3.7 实机取证），本组件不依赖它、先于任何 GTK 初始化执行。
/// </summary>
/// <remarks>
/// 平台边界：Linux/macOS 启用；Windows 无验证环境不启用（调用方按 OS 分支放行）。
/// 崩溃残留的 socket 文件由「connect 探活失败即删除重建」自愈路径处理；残留文件清不掉时
/// 降级为无监听主实例照常启动——仲裁是增强能力，绝不挡启动、绝不造成零实例。
/// </remarks>
public static class LauncherActivation
{
    /// <summary>二启通知命令行。</summary>
    public const string ShowCommand = "show";

    /// <summary>主实例对 <see cref="ShowCommand"/> 的应答。</summary>
    public const string AckResponse = "ok";

    /// <summary>锁地址：<paramref name="runtimeDir"/> 下应用名 + dev 隔离后缀（与 ApplicationId 同源规则）。
    /// dev 与正式版各持一把锁，互不顶牛。</summary>
    public static string SocketPath(string runtimeDir, string appName, bool isDev) =>
        Path.Combine(runtimeDir, $"{appName}{(isDev ? ".dev" : string.Empty)}.sock");

    /// <summary>尝试以主实例身份持有锁地址。true=主实例——锁地址持有成功并监听；
    /// 系统级 socket 异常或残留文件清理失败时降级为无监听空转（仲裁是增强能力，绝不挡启动，
    /// 降级原因见 <paramref name="log"/>）。false=地址被占且探活可达——存在存活的主实例，
    /// 调用方应走 <see cref="NotifyPrimary"/> 后退出。</summary>
    public static bool TryBindPrimary(
        string path,
        Func<Task> onShowRequested,
        Action<string>? log,
        out PrimaryListener? listener)
    {
        listener = null;
        if (OperatingSystem.IsWindows())
        {
            // Windows 无验证环境：不启用互斥，行为维持现状（ADR 平台边界）
            return true;
        }

        try
        {
            var socket = BindWithStaleRecovery(path, log);
            if (socket is null)
            {
                return false;
            }

            listener = new PrimaryListener(socket, path, onShowRequested, log);
            listener.Start();
            return true;
        }
        catch (Exception ex)
        {
            // 激活面是增强能力：socket 系统级异常降级为首实例无监听，绝不挡启动
            log?.Invoke($"[host] 单实例监听不可用（按主实例继续）：{ex.Message}");
            return true;
        }
    }

    /// <summary>向既有主实例发送显示主窗请求。返回是否收到应答；任何失败都返回 false，
    /// 调用方无论成败都应退出（绝不重复拉起运行时）。</summary>
    public static bool NotifyPrimary(string path, TimeSpan timeout)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(path);
            using var cts = new CancellationTokenSource(timeout);
            if (!socket.ConnectAsync(endpoint, cts.Token).AsTask().Wait(timeout))
            {
                // 连接超时：显式中止挂起连接再走释放，防孤儿连接任务与 cts 释放竞态
                cts.Cancel();
                return false;
            }

            var payload = System.Text.Encoding.UTF8.GetBytes(ShowCommand + "\n");
            socket.Send(payload);
            socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            var buffer = new byte[64];
            var read = socket.Receive(buffer);
            var ack = System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim();
            return ack == AckResponse;
        }
        catch (Exception)
        {
            // 吞掉连接/发送/接收全程的任何失败：协议契约是「任何失败都返回 false」，
            // 调用方无论成败都退出（绝不重复拉起运行时），故无需区分失败形态
            return false;
        }
    }

    /// <summary>bind 前对残留 socket 文件自愈：connect 探活可达=真有主实例（返回 null 走二启）；
    /// 文件存在但不可达=上次崩溃残留，删除后重建；bind 与探活之间的窗口被抢按有主处理。</summary>
    private static Socket? BindWithStaleRecovery(string path, Action<string>? log)
    {
        if (File.Exists(path))
        {
            if (ProbeAlive(path))
            {
                return null;
            }

            // 残留文件必须先移除：UDS bind 到已存在路径一律 EADDRINUSE
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                // 清不掉就无法持锁：抛给外层按「监听不可用」降级为无监听主实例——绝不挡启动，
                // 也绝不冒充「有存活主实例」让二启陪葬成零实例（return null 语义只留给真有主）
                log?.Invoke($"[host] 单实例残留锁清理失败，将降级为无监听主实例：{ex.Message}");
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // 探活与 bind 之间的窗口被抢：按有主处理
            socket.Dispose();
            return null;
        }

        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            try
            {
                // 收紧权限：仅当前用户可连（UDS 文件系统权限位）；部分文件系统不支持则跳过
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // 权限收紧失败不影响功能
            }
        }

        // bind 之后必须 listen：缺它 connect 一律 ECONNREFUSED、accept 无法工作
        socket.Listen();
        log?.Invoke("[host] 单实例锁已持有");
        return socket;
    }

    private static bool ProbeAlive(string path)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
            return probe.Connected;
        }
        catch (Exception)
        {
            // 探活把一切失败（ENOENT/ECONNREFUSED/权限拒绝…）统一视为「对端不在」：
            // 这是残留文件判定的唯一依据，失败形态无需区分
            return false;
        }
    }
}

/// <summary>主实例侧的监听句柄：accept 循环读一行命令，<see cref="LauncherActivation.ShowCommand"/>
/// 则回 <see cref="LauncherActivation.AckResponse"/> 并触发显示主窗回调；其余输入静默丢弃不断链。
/// Dispose 取消循环并 unlink 锁文件；幂等，重复调用安全。</summary>
public sealed class PrimaryListener : IDisposable
{
    private readonly Socket _socket;
    private readonly string _path;
    private readonly Func<Task> _onShowRequested;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    internal PrimaryListener(Socket socket, string path, Func<Task> onShowRequested, Action<string>? log)
    {
        _socket = socket;
        _path = path;
        _onShowRequested = onShowRequested;
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
                var client = await _socket.AcceptAsync(ct).ConfigureAwait(false);
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
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var buffer = new byte[256];
                var read = await client.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                var command = System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim();
                if (command != LauncherActivation.ShowCommand)
                {
                    return;
                }

                var ack = System.Text.Encoding.UTF8.GetBytes(LauncherActivation.AckResponse);
                await client.SendAsync(ack, cts.Token).ConfigureAwait(false);
            }

            // 应答已先行发出（ack=请求受理而非执行结果）；回调 await 到完成，
            // 失败由本方法 catch 记日志——异步段异常不再逃逸
            await _onShowRequested().ConfigureAwait(false);
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
