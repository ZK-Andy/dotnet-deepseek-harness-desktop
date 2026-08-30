namespace DeepSeek.Harness.Desktop.Services;

/// <summary>
/// 运行时监督：dsh 子进程退出时自动恢复——先展示恢复屏，再重启 dsh，拿到新 URL 后导航 WebView 到新地址。
/// 只重启子进程、不重启桌面进程（对应 proposed architecture ADR 的崩溃恢复）。
/// </summary>
public sealed class RuntimeSupervisor
{
    private readonly HarnessRuntimeHost _host;
    private readonly TimeSpan _restartTimeout;
    private readonly Func<ValueTask> _showRecovery;
    private readonly Func<Uri, ValueTask> _navigate;
    private readonly Action<string>? _log;

    /// <summary>创建监督器。</summary>
    /// <param name="host">运行时宿主。</param>
    /// <param name="restartTimeout">单次重启等待 URL 的时限。</param>
    /// <param name="showRecovery">展示恢复屏（如 WebView 显示"重启中"页）。</param>
    /// <param name="navigate">导航 WebView 到新 URL。</param>
    /// <param name="log">日志回调（可选）。</param>
    public RuntimeSupervisor(
        HarnessRuntimeHost host,
        TimeSpan restartTimeout,
        Func<ValueTask> showRecovery,
        Func<Uri, ValueTask> navigate,
        Action<string>? log = null)
    {
        _host = host;
        _restartTimeout = restartTimeout;
        _showRecovery = showRecovery;
        _navigate = navigate;
        _log = log;
    }

    /// <summary>循环监督直到 <paramref name="ct"/> 取消。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await AwaitChildExitOrCancelAsync(ct);
            if (ct.IsCancellationRequested)
            {
                break;
            }

            _log?.Invoke("[supervisor] dsh 子进程退出，执行恢复…");
            // 子进程死前 stderr 只存内存尾巴，随进程消失——趁恢复前落盘留证
            IReadOnlyList<string> stderrTail = _host.StderrTail;
            if (stderrTail.Count > 0)
            {
                _log?.Invoke($"[supervisor] 子进程 stderr 尾部：\n{string.Join('\n', stderrTail.TakeLast(8))}");
            }

            try
            {
                await _showRecovery();
                Uri? url = await _host.RestartAsync(_restartTimeout, ct);
                if (url is not null)
                {
                    _log?.Invoke($"[supervisor] 重启成功 → {url}");
                    await _navigate(url);
                }
                else
                {
                    _log?.Invoke("[supervisor] 重启未给出 URL，2s 后重试");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[supervisor] 恢复失败：{ex.Message}（1s 后重试）");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task AwaitChildExitOrCancelAsync(CancellationToken ct)
    {
        Task exit = _host.WaitForExitAsync();
        if (exit.IsCompleted)
        {
            return;
        }

        var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration reg = ct.Register(() => cancelTcs.TrySetResult());
        await Task.WhenAny(exit, cancelTcs.Task);
    }
}
