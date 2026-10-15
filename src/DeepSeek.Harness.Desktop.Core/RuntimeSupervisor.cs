namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// 运行时监督（ADR runtime-supervisor-core-port：监督策略属 Core，宿主交互经 <see cref="IRuntimeHost"/> 端口）：
/// dsh 子进程退出时自动恢复——先展示失败面，再重启 dsh，拿到新 URL 后交壳侧（重验铸币态，并按承载面
/// 是否覆写过活动页决定是否补一次收口导航；本类自身不碰窗口，ADR 2026-10-14-recovery-navigation-gate-retirement）。
/// 只重启子进程、不重启桌面进程（对应 proposed architecture ADR 的崩溃恢复）。
/// </summary>
public sealed class RuntimeSupervisor
{
    private readonly IRuntimeHost _host;
    private readonly TimeSpan _restartTimeout;
    private readonly TimeSpan _recoveredRetryDelay;
    private readonly TimeSpan _failedRetryDelay;
    private readonly int _blockedLogEveryRounds;
    private readonly Func<bool, ValueTask> _showRecovery;
    private readonly Func<Uri, ValueTask> _onRecovered;
    private readonly Action<string>? _log;

    /// <summary>创建监督器。</summary>
    /// <param name="host">运行时宿主端口。</param>
    /// <param name="restartTimeout">单次重启等待 URL 的时限。</param>
    /// <param name="recoveredRetryDelay">重启未给出 URL 后的重试延迟（可调参数，见 <c>Infrastructure.Runtime.RuntimeTimeouts</c>）。</param>
    /// <param name="failedRetryDelay">恢复失败后的重试延迟（可调参数，同上）。</param>
    /// <param name="blockedLogEveryRounds">残留锁死同因重试的留痕步长：首轮必留痕，此后每隔该轮数
    /// 留痕一次（可调参数，同上；锁死重探本身仍按 failedRetryDelay 每轮执行，用户清理后秒级恢复）。</param>
    /// <param name="showRecovery">展示失败面：参数为是否因残留锁死而展示（true = 带锁原因，
    /// 失败面即 fail loud 界面；false = 普通崩溃原因）。</param>
    /// <param name="onRecovered">恢复回调：重启给出新 URL 后调用。壳侧据此重验铸币态（epoch 可能已换：
    /// 新 secret/端口），并按失败面是否覆写过活动页决定是否补一次收口导航——**由壳侧实现决定，本类不自持窗口**
    /// （ADR 2026-10-14-recovery-navigation-gate-retirement）。</param>
    /// <param name="log">日志回调（可选）。</param>
    public RuntimeSupervisor(
        IRuntimeHost host,
        TimeSpan restartTimeout,
        TimeSpan recoveredRetryDelay,
        TimeSpan failedRetryDelay,
        Func<bool, ValueTask> showRecovery,
        Func<Uri, ValueTask> onRecovered,
        Action<string>? log = null,
        int blockedLogEveryRounds = 60)
    {
        _host = host;
        _restartTimeout = restartTimeout;
        _recoveredRetryDelay = recoveredRetryDelay;
        _failedRetryDelay = failedRetryDelay;
        _blockedLogEveryRounds = blockedLogEveryRounds;
        _showRecovery = showRecovery;
        _onRecovered = onRecovered;
        _log = log;
    }

    /// <summary>循环监督直到 <paramref name="ct"/> 取消。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // 残留锁死的连续轮数（解锁即清零）：同因重试的日志节流依据（ADR pid-reuse-foreign-cmdline-clear——
        // 实机事故：跨重启 stale 记录撞无关进程，锁死轮以 1s 节拍连刷 1100+ 条同文日志）。
        int blockedRounds = 0;
        while (!ct.IsCancellationRequested)
        {
            await AwaitChildExitOrCancelAsync(ct);
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                // 残留预检（ADR residue-lock-fail-loud）：verified 僵尸顺手杀；杀不掉/不敢杀
                // 即跳过本轮重启——盲目 spawn 只会端口碰撞。失败面带锁原因，failedRetryDelay 后重探
                // （用户手动清理后自动恢复）。重探每轮照跑，只有同因留痕按步长节流（首轮必留痕）。
                if (_host.TryDetectUnreapableResidue())
                {
                    blockedRounds++;
                    if (blockedRounds == 1 || blockedRounds % _blockedLogEveryRounds == 0)
                    {
                        _log?.Invoke("[supervisor] dsh 子进程退出，执行恢复…");
                        LogStderrTail();
                        _log?.Invoke("[supervisor] 残留无法安全回收，跳过本轮重启（fail loud，锁原因已交失败面按卡住阈值门控）");
                    }

                    await _showRecovery(true);
                    await Task.Delay(_failedRetryDelay, ct);
                    continue;
                }

                blockedRounds = 0;
                _log?.Invoke("[supervisor] dsh 子进程退出，执行恢复…");
                LogStderrTail();

                await _showRecovery(false);
                Uri? url = await _host.RestartAsync(_restartTimeout, ct);
                if (url is not null)
                {
                    _log?.Invoke($"[supervisor] 重启成功 → {url}");
                    await _onRecovered(url);
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

    /// <summary>子进程死前 stderr 只存内存尾巴，随进程消失——趁恢复前落盘留证。</summary>
    private void LogStderrTail()
    {
        IReadOnlyList<string> stderrTail = _host.StderrTail;
        if (stderrTail.Count > 0)
        {
            _log?.Invoke($"[supervisor] 子进程 stderr 尾部：\n{string.Join('\n', stderrTail.TakeLast(8))}");
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
