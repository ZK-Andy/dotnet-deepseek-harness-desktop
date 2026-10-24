using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>
/// PagePump 注入契约（ADR pagepump-retry-helper-unification）：横幅与引导进度共用「未就绪按节拍重试、
/// 其余异常/已销毁/取消一律放弃、绝不上抛」的循环骨架；结论横幅另带 holder 落点判据
/// （ADR avalonia-g3-update-surface——holder 页 <c>location.reload()</c> 会吞掉注入）。
/// </summary>
public class PagePumpTests
{
    /// <summary>被测单点（默认超时配置；实例形态由 ADR 组合根寿命与配置单一来源 D4 引入）。</summary>
    private static readonly PagePump s_pump = new(new RuntimeTimeouts());

    /// <summary>结论横幅的落点判据（ADR avalonia-g3-update-surface）：holder 文档上不注入，
    /// 按节拍重试到真 UI 出现后再注入——否则横幅会随 holder 的 <c>location.reload()</c> 消失。</summary>
    [Fact]
    public async Task ShowBannerWhenShellReadyAsync_WaitsForHolderToLeave()
    {
        var target = new FakeWindowScriptTarget();
        target.EnqueueResult("holder", "ui");
        PagePump pump = FastPump();

        await pump.ShowBannerWhenShellReadyAsync(target, "BANNER", WebAuthRecovery.HolderMarker, CancellationToken.None);

        Assert.Equal(3, target.Scripts.Count);
        Assert.Contains("holder", target.Scripts[0]);
        Assert.Equal("BANNER", target.LastScript);
    }

    /// <summary>始终停在 holder：按次数预算放弃（留痕，不上抛）。</summary>
    [Fact]
    public async Task ShowBannerWhenShellReadyAsync_GivesUpWhenHolderPersists()
    {
        var target = new FakeWindowScriptTarget();
        target.EnqueueResult(Enumerable.Repeat("holder", 3).ToArray());
        PagePump pump = FastPump();

        await pump.ShowBannerWhenShellReadyAsync(target, "BANNER", WebAuthRecovery.HolderMarker, CancellationToken.None);

        Assert.DoesNotContain("BANNER", target.Scripts);
    }

    /// <summary>三次尝试、1s 节拍的注入单点（预算按 <c>WindowReadyTimeoutSeconds</c> 折算，节拍 0 会塌成 1 次）。</summary>
    private static PagePump FastPump() => new(new RuntimeTimeouts { BannerRetryDelaySeconds = 1, WindowReadyTimeoutSeconds = 3 });

    /// <summary>退出期目标已销毁：ObjectDisposedException 是 InvalidOperationException 的<b>子类</b>，
    /// 须先于它被捕获——否则会白跑满重试预算。本用例钉的是单点循环骨架（ADR
    /// pagepump-retry-helper-unification：「已销毁即放弃」对全部调用点生效）。</summary>
    [Fact]
    public async Task ShowBannerWhenShellReadyAsync_StopsWhenTargetDisposed()
    {
        var target = new FakeWindowScriptTarget();
        target.Enqueue(new ObjectDisposedException("bridge"));
        var pump = new PagePump(new RuntimeTimeouts { WindowReadyTimeoutSeconds = 3, BannerRetryDelaySeconds = 1 });

        await pump.ShowBannerWhenShellReadyAsync(target, "BANNER", WebAuthRecovery.HolderMarker, CancellationToken.None);

        Assert.Single(target.Scripts);
    }

    /// <summary>其余异常一次性放弃：横幅属增强面，失败不空转重试。</summary>
    [Fact]
    public async Task ShowBannerWhenShellReadyAsync_GivesUpOnOtherFailures()
    {
        var target = new FakeWindowScriptTarget();
        target.Enqueue(new HttpRequestException("boom"));
        var pump = new PagePump(new RuntimeTimeouts { WindowReadyTimeoutSeconds = 3, BannerRetryDelaySeconds = 1 });

        await pump.ShowBannerWhenShellReadyAsync(target, "BANNER", WebAuthRecovery.HolderMarker, CancellationToken.None);

        Assert.Single(target.Scripts);
    }

    /// <summary>已取消的令牌不再发起注入（监督器终止/退出路径）。</summary>
    [Fact]
    public async Task ShowBannerWhenShellReadyAsync_SkipsWhenCancelled()
    {
        var target = new FakeWindowScriptTarget();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await s_pump.ShowBannerWhenShellReadyAsync(target, "BANNER", WebAuthRecovery.HolderMarker, cts.Token);

        Assert.Empty(target.Scripts);
    }

    /// <summary>次数用尽出口：跑满次数即放弃且不留成功痕——「窗口久不就绪」时的终态。</summary>
    [Fact]
    public async Task ShowBannerWhenShellReadyAsync_GivesUpWhenAttemptsExhausted()
    {
        var target = new FakeWindowScriptTarget();
        target.Enqueue(
            new InvalidOperationException("not ready"),
            new InvalidOperationException("not ready"),
            new InvalidOperationException("not ready"));
        var pump = new PagePump(new RuntimeTimeouts { WindowReadyTimeoutSeconds = 3, BannerRetryDelaySeconds = 1 });

        await pump.ShowBannerWhenShellReadyAsync(target, "BANNER", WebAuthRecovery.HolderMarker, CancellationToken.None);

        Assert.Equal(3, target.Scripts.Count);
    }

    /// <summary>到点预算 → 次数折算（ADR pagepump-retry-helper-unification）：不读墙钟，纯算式故确定可复现。
    /// 前三条钉「与旧次数上限逐次一致」（次数×节拍往返不引入漂移）；后三条钉边界——节拍非正值单次尝试
    /// （防热循环）、到点非正值停用该路、商夹到 int 上限（int 上限秒 + 1ms 节拍可达 2.1e12）。</summary>
    /// <param name="deadlineSeconds">到点预算（秒）。</param>
    /// <param name="delayMilliseconds">节拍（毫秒）。</param>
    /// <param name="expected">期望次数。</param>
    [Theory]
    [InlineData(30, 1000, 30)]
    [InlineData(6, 400, 15)]
    [InlineData(60, 400, 150)]
    [InlineData(60, 0, 1)]
    [InlineData(0, 400, 0)]
    [InlineData(int.MaxValue, 1, int.MaxValue)]
    public void AttemptsForBudget_MapsDeadlineToAttempts(int deadlineSeconds, int delayMilliseconds, int expected) =>
        Assert.Equal(
            expected,
            PagePump.AttemptsForBudget(
                TimeSpan.FromSeconds(deadlineSeconds), TimeSpan.FromMilliseconds(delayMilliseconds)));

    /// <summary>自更新状态推送：把状态帧派发为页内事件（插件监听 <c>dsh-desktop-update</c>）。</summary>
    [Fact]
    public void PushUpdateState_Success_DispatchesUpdateEvent()
    {
        var target = new FakeWindowScriptTarget();

        PagePump.PushUpdateState(target, new UpdateState(UpdateStatus.Ready, Version: "1.2.3"));

        string script = Assert.Single(target.Scripts);
        Assert.Contains("dsh-desktop-update", script);
        Assert.Contains("1.2.3", script);
    }

    /// <summary>窗口未就绪：本次推送静默丢弃（状态机后续变化会再推），异常不外抛。</summary>
    [Fact]
    public void PushUpdateState_NotReady_DropsSilently()
    {
        var target = new FakeWindowScriptTarget();
        target.Enqueue(new InvalidOperationException("window not ready"));

        PagePump.PushUpdateState(target, new UpdateState(UpdateStatus.Ready, Version: "1.2.3"));

        Assert.Single(target.Scripts);
    }
}
