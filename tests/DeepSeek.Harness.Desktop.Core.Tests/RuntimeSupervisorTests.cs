namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>运行时监督器（ADR runtime-supervisor-core-port 后的首次单测，此前零覆盖）：
/// 经 <see cref="IRuntimeHost"/> fake 驱动四条恢复路径——正常重启恢复回调、残留锁死跳过、
/// 重启无 URL 重试、恢复异常退避；记序断言消费面（失败面先于恢复回调）。
/// 轮数由 fake 的退出信号数决定、停驻由 <c>Parked</c> 信号收口、重试延迟取零：不用墙钟预算换轮数——
/// 负载高的 windows-latest 上墙钟会先到期（实测单次实跑 929ms），轮数不足即误红。</summary>
public class RuntimeSupervisorTests
{
    // 挂死保险（不是轮数预算）：正常路径在 Parked 信号处取消，只有 fake 失灵才由该时限收口。
    private static readonly TimeSpan s_hangGuard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_parkTimeout = TimeSpan.FromSeconds(5);

    private sealed class FakeHost : IRuntimeHost
    {
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exitSignals;
        private int _restartCalls;

        public IReadOnlyList<string> StderrTail { get; init; } = Array.Empty<string>();
        public string RuntimeDescription => "fake dsh";
        public bool Residue { get; init; }
        public Uri? NextUrl { get; init; }
        public int ExitSignals { get; init; } = 1;

        /// <summary>首次 Restart 抛异常（恢复链路异常路径）；false 时正常返回 <see cref="NextUrl"/>。</summary>
        public bool FailFirstRestart { get; init; }

        /// <summary>循环已停驻信号：退出信号用尽后置位，测试据此取消而非按墙钟掐表。</summary>
        public Task Parked => _parked.Task;

        /// <summary>实际发生的重启次数（残留锁死路径应为 0）。</summary>
        public int RestartCalls => _restartCalls;

        public bool TryDetectUnreapableResidue() => Residue;

        public Task<Uri?> StartAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(NextUrl);

        public Task<Uri?> RestartAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            _restartCalls++;
            if (FailFirstRestart && _restartCalls == 1)
            {
                throw new InvalidOperationException("重启链路异常");
            }

            return Task.FromResult(NextUrl);
        }

        // 模拟子进程生命周期：前 N 次调用立即完成（每次=一次子进程退出），之后停驻
        // （生产语义：宿主等真实子进程退出；监督循环据此停驻，不空转）。
        public Task WaitForExitAsync()
        {
            if (_exitSignals++ < ExitSignals)
            {
                return Task.CompletedTask;
            }

            _parked.TrySetResult();
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
    }

    /// <summary>消费面记序：order 记失败面/恢复回调事件序，recovered 记恢复回调靶点，logs 记宿主日志。</summary>
    private sealed record SupervisorTrace(List<string> Order, List<Uri> Recovered, List<string> Logs);

    private static (RuntimeSupervisor Supervisor, SupervisorTrace Trace) Supervisor(
        IRuntimeHost host, int blockedLogEveryRounds = 60)
    {
        var order = new List<string>();
        var recovered = new List<Uri>();
        var logs = new List<string>();
        // 重试延迟取零：轮数只由 ExitSignals 决定，任何真实等待都只会把吞吐重新引入断言。
        TimeSpan d = TimeSpan.Zero;
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
            onRecovered: url =>
            {
                order.Add($"recover:{url}");
                recovered.Add(url);
                return ValueTask.CompletedTask;
            },
            log: logs.Add,
            blockedLogEveryRounds: blockedLogEveryRounds);
        return (supervisor, new SupervisorTrace(order, recovered, logs));
    }

    private static Uri Url(string name) => new($"http://127.0.0.1/{name}", UriKind.Absolute);

    // 启动监督循环，等 fake 宿主停驻（退出信号用尽）后取消，再等循环收口。
    // Parked 未按时到达也先取消：轮数完全由 ExitSignals 决定；循环收口由 s_hangGuard 兜底，
    // 故 fake 失灵（循环不响应取消）会在这里 loud 超时，不会把测试挂死。
    private static async Task RunUntilParkedAsync(RuntimeSupervisor supervisor, Task parked, CancellationTokenSource cts)
    {
        Task run = supervisor.RunAsync(cts.Token);
        try
        {
            await parked.WaitAsync(s_parkTimeout);
        }
        finally
        {
            cts.Cancel();
        }

        await run.WaitAsync(s_hangGuard);
    }

    /// <summary>正常恢复：退出 → 失败面（普通原因）→ 重启给出 URL → 恢复回调（失败面先于回调的记序契约）。
    /// 监督器只上报「重启已给出新 URL」，是否收口导航由壳侧按承载面决定（ADR 2026-10-14-recovery-navigation-gate-retirement），
    /// 故此处钉「回调被调用」；ExitSignals=1 → 恰好一轮退出，停驻后取消，不靠墙钟凑轮数。</summary>
    [Fact]
    public async Task RunAsync_RestartsAndNotifiesRecovery_OnChildExit()
    {
        using var cts = new CancellationTokenSource(s_hangGuard);
        var host = new FakeHost { NextUrl = Url("new-boot"), StderrTail = new[] { "boom" } };
        (RuntimeSupervisor supervisor, SupervisorTrace trace) = Supervisor(host);

        await RunUntilParkedAsync(supervisor, host.Parked, cts);

        Assert.Single(trace.Recovered);
        Assert.Equal(Url("new-boot"), trace.Recovered[0]);
        Assert.Equal(new[] { "recovery", $"recover:{Url("new-boot")}" }, trace.Order);

        // 子进程 stderr 尾巴随进程消失：恢复前必须落盘留证（唯一非空分支的执行点）。
        Assert.Contains(trace.Logs, l => l.Contains("子进程 stderr 尾部") && l.Contains("boom"));
    }

    /// <summary>残留锁死（ADR residue-lock-fail-loud）：跳过本轮重启（不回调），失败面带锁原因，退避后重探。
    /// ExitSignals=1 → 恰好一轮锁死、零次重启。</summary>
    [Fact]
    public async Task RunAsync_LockBlocked_SkipsRestartButShowsRecovery()
    {
        using var cts = new CancellationTokenSource(s_hangGuard);
        var host = new FakeHost { Residue = true };
        (RuntimeSupervisor supervisor, SupervisorTrace trace) = Supervisor(host);

        await RunUntilParkedAsync(supervisor, host.Parked, cts);

        Assert.Empty(trace.Recovered);
        Assert.Equal(0, host.RestartCalls);
        Assert.Equal(new[] { "recovery:lock" }, trace.Order);
    }

    /// <summary>锁死同因重试的日志节流（ADR pid-reuse-foreign-cmdline-clear）：重探每轮照跑，但同因留痕
    /// 首轮一次 + 按步长再现。ExitSignals=6 + blockedLogEveryRounds=3 → 恰好 6 轮锁死、恰好 3 条同因
    /// 留痕（blockedRounds 为 1/3/6 的轮次）：步长只节流留痕，不减少重探轮数。</summary>
    [Fact]
    public async Task RunAsync_LockBlocked_LogsThrottledByStride()
    {
        using var cts = new CancellationTokenSource(s_hangGuard);
        var host = new FakeHost { Residue = true, ExitSignals = 6 };
        (RuntimeSupervisor supervisor, SupervisorTrace trace) =
            Supervisor(host, blockedLogEveryRounds: 3);

        await RunUntilParkedAsync(supervisor, host.Parked, cts);

        Assert.Equal(6, trace.Order.Count(o => o == "recovery:lock"));
        Assert.Equal(3, trace.Logs.Count(l => l.Contains("残留无法安全回收")));
        Assert.Equal(0, host.RestartCalls);
    }

    /// <summary>重启未给出 URL：不回调，走 recoveredRetryDelay 重试（失败面先于重试）。
    /// ExitSignals=3 → 恰好 3 轮「失败面 → 重启 → 重试」。</summary>
    [Fact]
    public async Task RunAsync_NoUrlAfterRestart_RetriesWithoutRecoveryCallback()
    {
        using var cts = new CancellationTokenSource(s_hangGuard);
        var host = new FakeHost { NextUrl = null, ExitSignals = 3 };
        (RuntimeSupervisor supervisor, SupervisorTrace trace) = Supervisor(host);

        await RunUntilParkedAsync(supervisor, host.Parked, cts);

        Assert.Empty(trace.Recovered);
        Assert.Equal(3, trace.Order.Count(o => o == "recovery"));
        Assert.Equal(3, host.RestartCalls);
        Assert.Equal(3, trace.Logs.Count(l => l.Contains("重启未给出 URL")));
    }

    /// <summary>恢复链路异常：loud 记日志后按 failedRetryDelay 退避，监督循环不崩（持续监督直到取消）。
    /// 首轮重启抛异常、次轮照跑（ExitSignals=2 + FailFirstRestart）→ 恰好 2 次失败面 + 1 条恢复失败
    /// 日志 + 1 条「重启未给出 URL」（次轮返回 null）。</summary>
    [Fact]
    public async Task RunAsync_RecoveryFault_KeepsSupervisingUntilCancel()
    {
        using var cts = new CancellationTokenSource(s_hangGuard);
        var flaky = new FakeHost { NextUrl = null, ExitSignals = 2, FailFirstRestart = true };
        (RuntimeSupervisor supervisor, SupervisorTrace trace) = Supervisor(flaky);

        await RunUntilParkedAsync(supervisor, flaky.Parked, cts);

        Assert.Empty(trace.Recovered);
        Assert.Equal(new[] { "recovery", "recovery" }, trace.Order);
        Assert.Equal(1, trace.Logs.Count(l => l.Contains("恢复失败")));
        Assert.Equal(1, trace.Logs.Count(l => l.Contains("重启未给出 URL")));
    }
}
