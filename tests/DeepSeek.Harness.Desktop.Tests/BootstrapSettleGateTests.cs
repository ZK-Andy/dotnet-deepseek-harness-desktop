namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>引导落定握手记序（批次 0 安全网，ADR composition-root-value-flow-pipeline）：
/// 钉住「引导落定先于监督器进入监视/横幅告知」的握手语义——批次 1 把 TCS 私有化为引导服务
/// 的类型化完成句柄时，此网抓时序漂移。</summary>
public class BootstrapSettleGateTests
{
    /// <summary>无引导路径（settled 为 null，dsh 已在 PATH）立即放行，不得为等句柄而空等。</summary>
    [Fact]
    public async Task NullSettled_ProceedsImmediately()
    {
        Assert.True(await BootstrapSettleGate.WaitSettledAsync(
            settled: null, timeout: TimeSpan.FromSeconds(30), ct: CancellationToken.None));
    }

    /// <summary>句柄已置位时立即放行——引导先行完成（后台任务跑赢接线）不丢放行。</summary>
    [Fact]
    public async Task AlreadySet_ProceedsImmediately()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settled.TrySetResult();

        Assert.True(await BootstrapSettleGate.WaitSettledAsync(
            settled, timeout: null, ct: CancellationToken.None));
    }

    /// <summary>句柄未置位时门保持等待（监督器不得提前进入监视），置位后才放行——握手次序本体。</summary>
    [Fact]
    public async Task SetWhileWaiting_GatesUntilSet()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> gate = BootstrapSettleGate.WaitSettledAsync(settled, timeout: null, CancellationToken.None);

        await Task.Delay(50);
        Assert.False(gate.IsCompleted, "引导落定前监督器/横幅不得放行");

        settled.TrySetResult();
        Assert.True(await gate);
    }

    /// <summary>应用退出（取消）返回 false：监督器直接返回不进监视、横幅放弃，不抛 OCE 出门。</summary>
    [Fact]
    public async Task CancelWhileWaiting_ReturnsFalseWithoutThrowing()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        Task<bool> gate = BootstrapSettleGate.WaitSettledAsync(settled, timeout: null, cts.Token);
        cts.Cancel();

        Assert.False(await gate);
    }

    /// <summary>超时按已定继续（降级语义）：引导迟迟未定时横幅照常告知，不因超时永久缺席。</summary>
    [Fact]
    public async Task Timeout_ProceedsDegraded()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(await BootstrapSettleGate.WaitSettledAsync(
            settled, timeout: TimeSpan.FromMilliseconds(50), ct: CancellationToken.None));
    }
}
