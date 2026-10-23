namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>G0 起步壳的并发收口契约（ADR avalonia-g0-entry-switch）：单次触发、关窗取消、
/// 未触发即释放。启动体以委托注入，覆盖真实入口的 Begin/Dispose 两条路径。</summary>
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

    /// <summary>启动体抛异常：留痕后发 Failed（失败面据此接管），事件负载是原异常。</summary>
    [Fact]
    public async Task Failure_RaisesFailedWithOriginalException()
    {
        var boom = new InvalidOperationException("spawn boom");
        var seen = new TaskCompletionSource<Exception>();
        using var startup = new ShellStartup(_ => throw boom, initialUri: null);
        startup.Failed += ex => seen.TrySetResult(ex);

        startup.Begin();

        Assert.Same(boom, await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>未 Begin 即 Dispose 不抛（退出路径幂等）。</summary>
    [Fact]
    public void Dispose_WithoutBegin_IsSafe()
    {
        new ShellStartup(_ => Task.CompletedTask, initialUri: null).Dispose();
    }
}
