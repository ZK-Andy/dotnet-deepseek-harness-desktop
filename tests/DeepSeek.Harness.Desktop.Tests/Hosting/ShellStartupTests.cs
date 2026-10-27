namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>起步壳的并发收口契约（ADR avalonia-g0-entry-switch；晚挂载语义见 ADR
/// 2026-10-26-cold-start-wait-collapse-and-proxy-cache-headers）：单次触发、关窗取消、未触发即释放，
/// 以及起步早于窗口就绪时「失败」与「收口后接线」各自只交付一次。</summary>
public class ShellStartupTests
{
    /// <summary>重复 Begin 只启动一次（单次 toc），且启动体确实执行。</summary>
    [Fact]
    public async Task Begin_RunsTheBodyExactlyOnce()
    {
        TaskCompletionSource started = new();
        int runs = 0;
        using var startup = new ShellStartup(
            _ =>
            {
                Interlocked.Increment(ref runs);
                started.TrySetResult();
                return Task.CompletedTask;
            },
            initialUri: null);

        startup.Begin();
        startup.Begin();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref runs));
    }

    /// <summary>Dispose 会取消在途启动体并有限等待收口。</summary>
    [Fact]
    public async Task Dispose_CancelsAnInFlightBody()
    {
        TaskCompletionSource started = new();
        TaskCompletionSource canceled = new();
        var startup = new ShellStartup(
            async ct =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult();
                    throw;
                }
            },
            initialUri: null);

        startup.Begin();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        startup.Dispose();

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>失败早于失败面挂载：挂上处置时补交付一次，负载是原异常（起步只跑一次，早到的失败没有第二次机会）。</summary>
    [Fact]
    public async Task Failure_BeforeSinkAttached_IsDeliveredOnAttach()
    {
        var boom = new InvalidOperationException("spawn boom");
        using var startup = new ShellStartup(_ => throw boom, initialUri: null);
        startup.Begin();
        await startup.Completion!.WaitAsync(TimeSpan.FromSeconds(5));

        var seen = new List<Exception>();
        startup.AttachFailureSink(seen.Add);

        Assert.Same(boom, Assert.Single(seen));
    }

    /// <summary>未 Begin 即 Dispose 不抛（退出路径幂等）。</summary>
    [Fact]
    public void Dispose_WithoutBegin_IsSafe()
    {
        new ShellStartup(_ => Task.CompletedTask, initialUri: null).Dispose();
    }

    /// <summary>起步仍在跑时挂接线：成功返回后执行一次。</summary>
    [Fact]
    public async Task AttachAfterStart_WhileRunning_RunsOnceOnSuccess()
    {
        var gate = new TaskCompletionSource();
        using var startup = new ShellStartup(_ => gate.Task, initialUri: null);
        int calls = 0;
        startup.Begin();
        startup.AttachAfterStart(() => Interlocked.Increment(ref calls));

        gate.SetResult();
        await startup.Completion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    /// <summary>起步已收口后挂接线：立即执行且只执行一次（窗口就绪晚于起步成功的形态）。</summary>
    [Fact]
    public async Task AttachAfterStart_AfterCompletion_RunsImmediatelyOnce()
    {
        using var startup = new ShellStartup(_ => Task.CompletedTask, initialUri: null);
        startup.Begin();
        await startup.Completion!.WaitAsync(TimeSpan.FromSeconds(5));

        int calls = 0;
        startup.AttachAfterStart(() => Interlocked.Increment(ref calls));
        startup.AttachAfterStart(() => Interlocked.Increment(ref calls));

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    /// <summary>起步体抛异常时不执行接线回调：失败面接管，监督器不得在未就绪的宿主上再 spawn
    /// （收口后补挂同样不执行）。</summary>
    [Fact]
    public async Task Failure_SkipsAfterStart()
    {
        var boom = new InvalidOperationException("spawn boom");
        var seen = new TaskCompletionSource<Exception>();
        int afterCalls = 0;
        using var startup = new ShellStartup(_ => throw boom, initialUri: null);
        startup.AttachFailureSink(ex => seen.TrySetResult(ex));
        startup.AttachAfterStart(() => Interlocked.Increment(ref afterCalls));

        startup.Begin();

        Assert.Same(boom, await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await startup.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
        startup.AttachAfterStart(() => Interlocked.Increment(ref afterCalls));
        Assert.Equal(0, Volatile.Read(ref afterCalls));
    }

    /// <summary>关窗取消的起步体不得执行接线回调（退出路径不再拉起引导/监督）。</summary>
    [Fact]
    public async Task CanceledBody_DoesNotInvokeAfterStart()
    {
        TaskCompletionSource started = new();
        int afterCalls = 0;
        var startup = new ShellStartup(
            async ct =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            initialUri: null);

        startup.AttachAfterStart(() => Interlocked.Increment(ref afterCalls));
        startup.Begin();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        startup.Dispose();
        await startup.Completion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Volatile.Read(ref afterCalls));
    }
}
