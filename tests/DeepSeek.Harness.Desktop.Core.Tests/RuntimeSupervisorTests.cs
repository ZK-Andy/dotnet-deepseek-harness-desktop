namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>运行时监督器（ADR runtime-supervisor-core-port 后的首次单测，此前零覆盖）：
/// 经 <see cref="IRuntimeHost"/> fake 驱动四条恢复路径——正常重启导航、残留锁死跳过、
/// 重启无 URL 重试、恢复异常退避；记序断言消费面（恢复屏先于导航）。</summary>
public class RuntimeSupervisorTests
{
    private sealed class FakeHost : IRuntimeHost
    {
        private TaskCompletionSource? _parked;
        private int _exitSignals;
        public IReadOnlyList<string> StderrTail { get; init; } = Array.Empty<string>();
        public string RuntimeDescription => "fake dsh";
        public bool TryDetectUnreapableResidue() => Residue;
        public Task<Uri?> StartAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(NextUrl);
        public Task<Uri?> RestartAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(NextUrl);

        // 模拟子进程生命周期：前 N 次调用立即完成（每次=一次子进程退出），之后挂起
        // （生产语义：宿主等真实子进程退出；监督循环据此停驻，不空转）。
        public Task WaitForExitAsync()
        {
            if (_exitSignals++ < ExitSignals)
            {
                return Task.CompletedTask;
            }

            _parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _parked.Task;
        }

        public bool Residue { get; init; }
        public Uri? NextUrl { get; init; }
        public int ExitSignals { get; init; } = 1;
    }

    private sealed class FlakyHost : IRuntimeHost
    {
        private int _calls;
        private int _exitSignals;
        public IReadOnlyList<string> StderrTail => Array.Empty<string>();
        public string RuntimeDescription => "fake dsh";
        public bool TryDetectUnreapableResidue() => false;

        public Task<Uri?> StartAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult<Uri?>(null);

        public Task<Uri?> RestartAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            _calls++;
            if (_calls == 1)
            {
                throw new InvalidOperationException("重启链路异常");
            }

            return Task.FromResult<Uri?>(null);
        }

        public Task WaitForExitAsync()
        {
            if (_exitSignals++ < 2)
            {
                return Task.CompletedTask;
            }

            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return parked.Task;
        }
    }

    /// <summary>消费面记序：order 记恢复屏/导航事件序，navigated 记导航靶点，logs 记宿主日志。</summary>
    private sealed record SupervisorTrace(List<string> Order, List<Uri> Navigated, List<string> Logs);

    private static (RuntimeSupervisor Supervisor, SupervisorTrace Trace) Supervisor(
        IRuntimeHost host, TimeSpan? delays = null, int blockedLogEveryRounds = 60)
    {
        var order = new List<string>();
        var navigated = new List<Uri>();
        var logs = new List<string>();
        TimeSpan d = delays ?? TimeSpan.FromMilliseconds(1);
        var supervisor = new RuntimeSupervisor(
            host,
            restartTimeout: d,
            recoveredRetryDelay: d,
            failedRetryDelay: d,
            showRecovery: lockBlocked =>
            {
                order.Add(lockBlocked ? "recovery:lock" : "recovery");
                return ValueTask.CompletedTask;
            },
            navigate: url =>
            {
                order.Add($"navigate:{url}");
                navigated.Add(url);
                return ValueTask.CompletedTask;
            },
            log: logs.Add,
            blockedLogEveryRounds: blockedLogEveryRounds);
        return (supervisor, new SupervisorTrace(order, navigated, logs));
    }

    private static Uri Url(string name) => new($"http://127.0.0.1/{name}", UriKind.Absolute);

    /// <summary>正常恢复：退出 → 恢复屏（普通原因）→ 重启给出 URL → 导航（恢复屏先于导航的记序契约）。</summary>
    [Fact]
    public async Task RunAsync_RestartsAndNavigates_OnChildExit()
    {
        using var cts = new CancellationTokenSource(2000);
        var host = new FakeHost { NextUrl = Url("new-boot"), StderrTail = new[] { "boom" } };
        (RuntimeSupervisor supervisor, SupervisorTrace trace) = Supervisor(host);

        await supervisor.RunAsync(cts.Token);

        Assert.Single(trace.Navigated);
        Assert.Equal(Url("new-boot"), trace.Navigated[0]);
        Assert.Equal("recovery", trace.Order[0]);
        Assert.True(trace.Order[0].StartsWith("recovery") && trace.Order[^1].StartsWith("navigate:"),
            $"恢复屏必须先于导航（记序）：{string.Join(",", trace.Order)}");
    }

    /// <summary>残留锁死（ADR residue-lock-fail-loud）：跳过本轮重启（不导航），恢复屏带锁原因，退避后重探。</summary>
    [Fact]
    public async Task RunAsync_LockBlocked_SkipsRestartButShowsRecovery()
    {
        using var cts = new CancellationTokenSource(300);
        var host = new FakeHost { Residue = true };
        (RuntimeSupervisor supervisor, SupervisorTrace trace) = Supervisor(host, TimeSpan.FromMilliseconds(50));

        await supervisor.RunAsync(cts.Token);

        Assert.Empty(trace.Navigated);
        Assert.Contains("recovery:lock", trace.Order);
        Assert.DoesNotContain(trace.Order, o => o.StartsWith("navigate:"));
        Assert.DoesNotContain(trace.Order, o => o == "recovery");
    }

    /// <summary>锁死同因重试的日志节流（ADR pid-reuse-foreign-cmdline-clear）：重探每轮照跑（恢复屏轮数
    /// 随轮次增长），但同因留痕首轮一次 + 按步长再现，同文日志远少于锁死轮数。</summary>
    [Fact]
    public async Task RunAsync_LockBlocked_LogsThrottledByStride()
    {
        using var cts = new CancellationTokenSource(500);
        var host = new FakeHost { Residue = true, ExitSignals = 100 };
        (RuntimeSupervisor supervisor, SupervisorTrace trace) =
            Supervisor(host, TimeSpan.FromMilliseconds(20), blockedLogEveryRounds: 3);

        await supervisor.RunAsync(cts.Token);

        int lockRounds = trace.Order.Count(o => o == "recovery:lock");
        int blockedLogs = trace.Logs.Count(l => l.Contains("残留无法安全回收"));
        Assert.True(lockRounds >= 5, $"节流不得减少重探轮数：{lockRounds}");
        Assert.True(blockedLogs >= 2, $"首轮与步长轮都应留痕：{blockedLogs}");
        Assert.True(blockedLogs < lockRounds, $"同因留痕须被节流（{blockedLogs} < {lockRounds}）");
    }

    /// <summary>重启未给出 URL：不导航，走 recoveredRetryDelay 重试（恢复屏先于重试）。</summary>
    [Fact]
    public async Task RunAsync_NoUrlAfterRestart_RetriesWithoutNavigate()
    {
        using var cts = new CancellationTokenSource(300);
        var host = new FakeHost { NextUrl = null, ExitSignals = 3 };
        (RuntimeSupervisor supervisor, SupervisorTrace trace) = Supervisor(host, TimeSpan.FromMilliseconds(50));

        await supervisor.RunAsync(cts.Token);

        Assert.Empty(trace.Navigated);
        Assert.True(trace.Order.Count(o => o == "recovery") >= 2, $"无 URL 应重试多轮：{string.Join(",", trace.Order)}");
    }

    /// <summary>恢复链路异常：loud 记日志后按 failedRetryDelay 退避，监督循环不崩（持续监督直到取消）。</summary>
    [Fact]
    public async Task RunAsync_RecoveryFault_KeepsSupervisingUntilCancel()
    {
        using var cts = new CancellationTokenSource(300);
        var flaky = new FlakyHost();
        (RuntimeSupervisor supervisor, SupervisorTrace trace) = Supervisor(flaky, TimeSpan.FromMilliseconds(50));

        await supervisor.RunAsync(cts.Token);

        Assert.Empty(trace.Navigated);
        Assert.True(trace.Order.Count >= 2, $"异常后应退避重试而非终止：{string.Join(",", trace.Order)}");
    }
}
