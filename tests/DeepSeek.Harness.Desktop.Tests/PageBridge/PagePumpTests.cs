using DeepSeek.Harness.Desktop.Tests.Fakes;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>
/// PagePump 注册式注入契约（ADR frameless-uniform-caption-bar「注入时机」条）：顶栏脚本另经
/// <c>IRynWebView.InjectScriptAsync</c> 注册为「每次页面加载执行」——三路里唯一不靠导航事件覆盖后续每次加载的一路
/// （holder 页 <c>location.reload()</c> 进真 UI 是同 URL 重载，mac/win 的导航回调不发）。就绪前的
/// InvalidOperationException（DeferredRynWebView 契约）须按节拍重试至次数用尽（注册路的次数由
/// <c>CaptionBarRegisterSeconds</c> 经 <c>AttemptsForBudget</c> 折算）；退出期销毁、其余异常与次数
/// 耗尽一律放弃、绝不上抛（四路共用同一循环，语义见 ADR pagepump-retry-helper-unification）。
/// </summary>
public class PagePumpTests
{
    /// <summary>被测单点（默认超时配置；实例形态由 ADR 组合根寿命与配置单一来源 D4 引入）。</summary>
    private static readonly PagePump s_pump = new(new RuntimeTimeouts());

    /// <summary>webview 就绪后一次注册成功：不重试、送进去的就是给的脚本。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_RegistersOnceWhenReady()
    {
        var view = new FakeWebView();

        await s_pump.RegisterCaptionBarScriptWhenReadyAsync(() => view, "SCRIPT", CancellationToken.None);

        Assert.Equal(1, view.InjectCalls);
        Assert.Equal("SCRIPT", view.LastScript);
    }

    /// <summary>就绪前抛 InvalidOperationException 按节拍重试、就绪后补上——「网页视图后于接线创建」
    /// 是常态（DeferredRynWebView.Live 的契约）。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_RetriesUntilWebViewReady()
    {
        var view = new FakeWebView();
        view.Enqueue(new InvalidOperationException("webview not ready"));

        await s_pump.RegisterCaptionBarScriptWhenReadyAsync(() => view, "SCRIPT", CancellationToken.None);

        Assert.Equal(2, view.InjectCalls);
    }

    /// <summary>退出期 webview 已销毁：ObjectDisposedException 是 InvalidOperationException 的<b>子类</b>，
    /// 须先于它被捕获——否则会白跑满重试预算。本用例钉的是四路共用的循环骨架（ADR
    /// pagepump-retry-helper-unification：「已销毁即放弃」由此对全部调用点生效）。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_StopsWhenWebViewDisposed()
    {
        var view = new FakeWebView();
        view.Enqueue(new ObjectDisposedException("webview"));

        await s_pump.RegisterCaptionBarScriptWhenReadyAsync(() => view, "SCRIPT", CancellationToken.None);

        Assert.Equal(1, view.InjectCalls);
    }

    /// <summary>其余异常一次性放弃：顶栏属增强面，注册失败不空转重试。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_GivesUpOnOtherFailures()
    {
        var view = new FakeWebView();
        view.Enqueue(new InvalidOperationException("not ready"));
        view.Enqueue(new HttpRequestException("boom"));

        await s_pump.RegisterCaptionBarScriptWhenReadyAsync(() => view, "SCRIPT", CancellationToken.None);

        Assert.Equal(2, view.InjectCalls);
    }

    /// <summary>已取消的令牌不再发起注册（监督器终止/退出路径）。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_SkipsWhenCancelled()
    {
        var view = new FakeWebView();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await s_pump.RegisterCaptionBarScriptWhenReadyAsync(() => view, "SCRIPT", cts.Token);

        Assert.Equal(0, view.InjectCalls);
    }

    /// <summary>次数用尽出口（可注入次数的重载；`GivesUpOnOtherFailures` 走的是其余异常分支，不是本出口）：
    /// 跑满次数即放弃且不留成功痕——这条是「网页视图久不就绪」时的终态。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_GivesUpWhenAttemptsExhausted()
    {
        var view = new FakeWebView();
        view.Enqueue(
            new InvalidOperationException("not ready"),
            new InvalidOperationException("not ready"),
            new InvalidOperationException("not ready"));

        await PagePump.RegisterCaptionBarScriptWhenReadyAsync(
            () => view, "SCRIPT", maxAttempts: 3, retryDelay: TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(3, view.InjectCalls);
        Assert.Null(view.LastScript);
    }

    /// <summary>次数预算 ≤0（配置把次数配成 0）即一次都不尝试：直接进入次数用尽终态，不空转。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_SkipsWhenNoAttemptsBudget()
    {
        var view = new FakeWebView();

        await PagePump.RegisterCaptionBarScriptWhenReadyAsync(
            () => view, "SCRIPT", maxAttempts: 0, retryDelay: TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.Equal(0, view.InjectCalls);
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

    /// <summary>脚本求值端口：窗口未就绪（InvalidOperationException）按节拍重试、就绪后补上——
    /// 原 accessor 分支的重试语义改由端口承载后同样可单测（ADR architecture/2026-10-21-window-script-target-port）。</summary>
    [Fact]
    public async Task InjectCaptionBarWhenReadyAsync_RetriesUntilTargetReady()
    {
        var target = new FakeWindowScriptTarget();
        target.Enqueue(new InvalidOperationException("window not ready"));
        var pump = new PagePump(new RuntimeTimeouts { PushMaxAttempts = 3, PushRetryDelayMilliseconds = 1 });

        await pump.InjectCaptionBarWhenReadyAsync(target, "SCRIPT", CancellationToken.None);

        Assert.Equal(2, target.Scripts.Count);
        Assert.Equal("SCRIPT", target.LastScript);
    }

    /// <summary>端口抛其余异常即一次性放弃（顶栏属增强面，不空转重试）。</summary>
    [Fact]
    public async Task InjectCaptionBarWhenReadyAsync_GivesUpOnOtherFailures()
    {
        var target = new FakeWindowScriptTarget();
        target.Enqueue(new HttpRequestException("boom"));
        var pump = new PagePump(new RuntimeTimeouts { PushMaxAttempts = 3, PushRetryDelayMilliseconds = 1 });

        await pump.InjectCaptionBarWhenReadyAsync(target, "SCRIPT", CancellationToken.None);

        Assert.Single(target.Scripts);
    }

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
