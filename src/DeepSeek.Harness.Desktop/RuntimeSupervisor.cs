namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 运行时监督：dsh 子进程退出时自动恢复——先展示恢复屏，再重启 dsh，拿到新 URL 后导航 WebView 到新地址。
/// 只重启子进程、不重启桌面进程（对应 proposed architecture ADR 的崩溃恢复）。
/// </summary>
public sealed class RuntimeSupervisor
{
    private readonly HarnessRuntimeHost _host;
    private readonly TimeSpan _restartTimeout;
    private readonly TimeSpan _recoveredRetryDelay;
    private readonly TimeSpan _failedRetryDelay;
    private readonly Func<bool, ValueTask> _showRecovery;
    private readonly Func<Uri, ValueTask> _navigate;
    private readonly Action<string>? _log;

    /// <summary>创建监督器。</summary>
    /// <param name="host">运行时宿主。</param>
    /// <param name="restartTimeout">单次重启等待 URL 的时限。</param>
    /// <param name="recoveredRetryDelay">重启未给出 URL 后的重试延迟（可调参数，见 <c>Infrastructure.Runtime.RuntimeTimeouts</c>）。</param>
    /// <param name="failedRetryDelay">恢复失败后的重试延迟（可调参数，同上）。</param>
    /// <param name="showRecovery">展示恢复屏：参数为是否因残留锁死而展示（true = 带锁原因，
    /// 恢复屏即 fail loud 界面；false = 普通崩溃原因）。</param>
    /// <param name="navigate">导航 WebView 到新 URL。</param>
    /// <param name="log">日志回调（可选）。</param>
    public RuntimeSupervisor(
        HarnessRuntimeHost host,
        TimeSpan restartTimeout,
        TimeSpan recoveredRetryDelay,
        TimeSpan failedRetryDelay,
        Func<bool, ValueTask> showRecovery,
        Func<Uri, ValueTask> navigate,
        Action<string>? log = null)
    {
        _host = host;
        _restartTimeout = restartTimeout;
        _recoveredRetryDelay = recoveredRetryDelay;
        _failedRetryDelay = failedRetryDelay;
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
                // 残留预检（ADR residue-lock-fail-loud）：verified 僵尸顺手杀；杀不掉/不敢杀
                // 即跳过本轮重启——盲目 spawn 只会端口碰撞。恢复屏带锁原因（恢复页提示），
                // failedRetryDelay 后重探（用户手动清理后自动恢复）
                if (_host.TryDetectUnreapableResidue())
                {
                    _log?.Invoke("[supervisor] 残留无法安全回收，跳过本轮重启（fail loud，恢复屏已带锁原因）");
                    await _showRecovery(true);
                    await Task.Delay(_failedRetryDelay, ct);
                    continue;
                }

                await _showRecovery(false);
                Uri? url = await _host.RestartAsync(_restartTimeout, ct);
                if (url is not null)
                {
                    _log?.Invoke($"[supervisor] 重启成功 → {url}");
                    await _navigate(url);
                }
                else
                {
                    _log?.Invoke($"[supervisor] 重启未给出 URL，{_recoveredRetryDelay.TotalSeconds:0}s 后重试");
                    await Task.Delay(_recoveredRetryDelay, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[supervisor] 恢复失败：{ex.Message}（{_failedRetryDelay.TotalSeconds:0}s 后重试）");
                try
                {
                    await Task.Delay(_failedRetryDelay, ct);
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
