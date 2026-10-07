using System.Text.Json.Serialization.Metadata;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop.Tests.PageBridge;

/// <summary>
/// PagePump 注册式注入契约（ADR frameless-uniform-caption-bar「注入时机」条）：顶栏脚本另经
/// <c>IRynWebView.InjectScriptAsync</c> 注册为「每次页面加载执行」——三路里唯一不靠导航事件覆盖后续每次加载的一路
/// （holder 页 <c>location.reload()</c> 进真 UI 是同 URL 重载，mac/win 的导航回调不发）。就绪前的
/// InvalidOperationException（DeferredRynWebView 契约）须按节拍重试；退出期销毁、其余异常与预算耗尽
/// 一律放弃，绝不外抛拖垮启动链路。
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
    /// 须先于它被捕获——否则会白跑满重试预算。</summary>
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

    /// <summary>预算耗尽出口（可注入预算的重载；`GivesUpOnOtherFailures` 走的是其余异常分支，不是本出口）：
    /// 跑满尝试次数后放弃、不再发起，且不留成功痕——这条是「网页视图久不就绪」时的终态。</summary>
    [Fact]
    public async Task RegisterCaptionBarScriptWhenReadyAsync_GivesUpWhenBudgetExhausted()
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
