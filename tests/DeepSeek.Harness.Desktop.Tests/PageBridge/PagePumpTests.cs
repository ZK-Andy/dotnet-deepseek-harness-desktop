using System.Text.Json.Serialization.Metadata;
using Ryn.Core;

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
    /// <summary>webview 就绪后一次注册成功：不重试、送进去的就是给的脚本。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_RegistersOnceWhenReady()
    {
        var view = new FakeWebView();

        await PagePump.RegisterCaptionBarScriptWhenReadyAsync(() => view, "SCRIPT", CancellationToken.None);

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

        await PagePump.RegisterCaptionBarScriptWhenReadyAsync(() => view, "SCRIPT", CancellationToken.None);

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

        await PagePump.RegisterCaptionBarScriptWhenReadyAsync(() => view, "SCRIPT", CancellationToken.None);

        Assert.Equal(1, view.InjectCalls);
    }

    /// <summary>其余异常一次性放弃：顶栏属增强面，注册失败不空转重试。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_GivesUpOnOtherFailures()
    {
        var view = new FakeWebView();
        view.Enqueue(new InvalidOperationException("not ready"));
        view.Enqueue(new HttpRequestException("boom"));

        await PagePump.RegisterCaptionBarScriptWhenReadyAsync(() => view, "SCRIPT", CancellationToken.None);

        Assert.Equal(2, view.InjectCalls);
    }

    /// <summary>已取消的令牌不再发起注册（监督器终止/退出路径）。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_SkipsWhenCancelled()
    {
        var view = new FakeWebView();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await PagePump.RegisterCaptionBarScriptWhenReadyAsync(() => view, "SCRIPT", cts.Token);

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

    /// <summary>IRynWebView 假体：本路径只消费 <c>InjectScriptAsync</c>，其余成员为接口完备性占位。</summary>
    private sealed class FakeWebView : IRynWebView
    {
        private readonly Queue<Exception?> _outcomes = new();

        /// <summary>未消费（显式存取器，免得「事件从未被使用」告警）。</summary>
        public event EventHandler<FileDropEventArgs>? FileDrop
        {
            add { }
            remove { }
        }

        /// <summary>注册调用次数（含失败尝试）。</summary>
        public int InjectCalls { get; private set; }

        /// <summary>最后一次成功送进的脚本。</summary>
        public string? LastScript { get; private set; }

        /// <summary>按序排定每次注册要抛的异常；队空即注册成功。</summary>
        /// <param name="outcomes">结局序列（异常；空表示成功）。</param>
        public void Enqueue(params Exception[] outcomes)
        {
            foreach (Exception outcome in outcomes)
            {
                _outcomes.Enqueue(outcome);
            }
        }

        /// <summary>注册脚本（按排定结局抛或成功）。</summary>
        /// <param name="script">待注册脚本。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>完成的任务。</returns>
        public ValueTask InjectScriptAsync(string script, CancellationToken cancellationToken = default)
        {
            InjectCalls++;
            Exception? outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : null;
            if (outcome is not null)
            {
                throw outcome;
            }

            LastScript = script;
            return ValueTask.CompletedTask;
        }

        /// <summary>未消费。</summary>
        /// <param name="url">目标 URL。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>完成的任务。</returns>
        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        /// <summary>未消费。</summary>
        /// <param name="html">页面 HTML。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>完成的任务。</returns>
        public ValueTask NavigateToStringAsync(string html, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        /// <summary>未消费。</summary>
        /// <param name="script">脚本。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>空结果。</returns>
        public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
            new(string.Empty);

        /// <summary>未消费。</summary>
        /// <param name="scheme">自定义 scheme。</param>
        /// <param name="handler">处理器。</param>
        public void RegisterCustomScheme(string scheme, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler)
        {
        }

        /// <summary>未消费。</summary>
        /// <param name="eventName">事件名。</param>
        /// <param name="jsonData">JSON 载荷。</param>
        public void EmitEvent(string eventName, string jsonData)
        {
        }

        /// <summary>未消费。</summary>
        /// <typeparam name="T">载荷类型。</typeparam>
        /// <param name="eventName">事件名。</param>
        /// <param name="payload">载荷。</param>
        /// <param name="typeInfo">源生成类型信息。</param>
        public void EmitEvent<T>(string eventName, T payload, JsonTypeInfo<T> typeInfo)
        {
        }
    }
}
