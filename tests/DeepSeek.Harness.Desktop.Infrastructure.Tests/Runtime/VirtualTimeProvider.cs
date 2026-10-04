namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Runtime;

/// <summary>
/// 虚拟时钟（ADR mint-gate-virtual-time-seam）：稳定化门控的「稳定窗达成 vs 预算耗尽」分叉
/// 只由本时钟的推进决定，与探活实际耗时/机器负载彻底解耦——墙钟竞速在 CI 并行负载下
/// 两次把稳定化通过误判成 fail-open（本地复现 1 次 + CI 1 次），调延迟/预算是打补丁不是修因。
/// 推进规则：仅 <see cref="CreateTimer"/>（即 <c>Task.Delay</c> 的等待）推进虚拟时间
/// 「dueTime 满量」；探活（真实回环 HTTP/WebSocket）不占虚拟时间。即时回调 + 满量推进
/// 保证每轮节拍的虚拟增量恰好等于 PollInterval，N 轮后预算耗尽的轮数是确定的。
/// </summary>
internal sealed class VirtualTimeProvider : TimeProvider
{
    // 自洽即正确：GetTimestamp 与 TimestampFrequency 同源（TimeSpan ticks = 10M/s），
    // GetElapsedTime 只在本类内部做差，与真实 Stopwatch 频率无关。
    private long _elapsedTicks;

    /// <summary>虚拟时间已推进量（测试诊断用）。</summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

    public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);

    public override long TimestampFrequency => 10_000_000;

    public override DateTimeOffset GetUtcNow() =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) + Elapsed;

    /// <summary>同步单发定时器：构造即推进虚拟时间并执行回调——Task.Delay(await) 的续延
    /// 拿到的是已完成任务，稳定化循环零真实等待，总耗时只剩探活的真实回环延迟。</summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime == Timeout.InfiniteTimeSpan)
        {
            throw new InvalidOperationException("虚拟时钟不支持无限等待（稳定化循环不会请求）");
        }

        if (dueTime > TimeSpan.Zero)
        {
            Interlocked.Add(ref _elapsedTicks, dueTime.Ticks);
        }

        callback(state);
        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
