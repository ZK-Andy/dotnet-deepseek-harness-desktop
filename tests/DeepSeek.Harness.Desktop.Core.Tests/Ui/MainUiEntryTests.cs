using DeepSeek.Harness.Desktop.Core.Ui;

namespace DeepSeek.Harness.Desktop.Core.Tests.Ui;

/// <summary>进入主界面用例（ADR 组合根机制收官 step-1 的 B1）：铸币 → 有界等窗 → 终页探针裁决 →
/// 鉴权页重铸并重载一次，经 fake 导航端口驱动——无代理源全跳过、健康页只铸一次不导航、鉴权页重铸重载一次、
/// 探针耗尽与等窗超时按降级继续、应用退出取消上抛。</summary>
public class MainUiEntryTests
{
    private static readonly Uri s_proxyUrl = new("http://127.0.0.1:5555/");

    /// <summary>fake 导航端口：窗口就绪可切、探针按调用序回放、导航即触发到达信号（提交等待因而必达）。</summary>
    private sealed class FakeNavigator : IWebViewNavigator
    {
        private readonly Func<int, string?> _probe;
        private Action? _arrived;
        private int _probes;

        public FakeNavigator(Func<int, string?>? probe = null) => _probe = probe ?? (_ => null);

        public bool WindowReady { get; set; } = true;

        public List<Uri> Navigated { get; } = [];

        public int ProbeCount => _probes;

        public bool IsWindowReady => WindowReady;

        public void SetArrivalCallback(Action onArrived) => _arrived = onArrived;

        public Task NavigateAsync(Uri target, CancellationToken ct)
        {
            Navigated.Add(target);
            _arrived?.Invoke();
            return Task.CompletedTask;
        }

        public Task<string?> EvaluateScriptAsync(string script) => Task.FromResult(_probe(++_probes));
    }

    /// <summary>探针永不完成的端口（覆盖探针超时耗尽出口）。</summary>
    private sealed class HangingProbeNavigator : IWebViewNavigator
    {
        public bool IsWindowReady => true;

        public void SetArrivalCallback(Action onArrived)
        {
        }

        public Task NavigateAsync(Uri target, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> EvaluateScriptAsync(string script) =>
            new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }

    /// <summary>导航调用永不返回、也不发到达信号的端口（覆盖「调用超时 + 提交等待超时」两条降级分支）。</summary>
    private sealed class HangingNavigateNavigator : IWebViewNavigator
    {
        private readonly string _sample;

        public HangingNavigateNavigator(string sample) => _sample = sample;

        public bool IsWindowReady => true;

        public List<Uri> Navigated { get; } = [];

        public void SetArrivalCallback(Action onArrived)
        {
            // 到达信号缺席：提交等待必须自己超时降级
        }

        public Task NavigateAsync(Uri target, CancellationToken ct)
        {
            Navigated.Add(target);
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }

        public Task<string?> EvaluateScriptAsync(string script) => Task.FromResult<string?>(_sample);
    }

    /// <summary>健康页采样（同源 + 非空文本 + 无鉴权/holder 标记）。</summary>
    private static string HealthySample() =>
        s_proxyUrl.GetLeftPart(UriPartial.Authority) + PageProbeSample.Separator + "DSH UI";

    /// <summary>鉴权页采样（命中鉴权标记，不限 origin）。</summary>
    private static string AuthSample() =>
        s_proxyUrl.GetLeftPart(UriPartial.Authority) + PageProbeSample.Separator + WebAuthRecovery.AuthRequiredMarker;

    /// <summary>无代理源（回环绑定失败）：铸币/探针/导航全跳过，只留降级行。</summary>
    [Fact]
    public async Task NoProxy_SkipsMintProbeAndNavigation()
    {
        var navigator = new FakeNavigator();
        var minted = new List<DshWebUrl>();
        var logs = new List<string>();

        await Create(navigator, minted, logs, proxyUrl: null)
            .RunAsync(Url(), CancellationToken.None);

        Assert.Empty(minted);
        Assert.Empty(navigator.Navigated);
        Assert.Contains(logs, line => line.Contains("进入主界面无代理源", StringComparison.Ordinal));
    }

    /// <summary>健康页：只铸一次币、终页裁决 healthy、不代发窗口导航（holder 自 reload 即进）。</summary>
    [Fact]
    public async Task HealthyPage_MintsOnce_LogsVerdict_NoNavigation()
    {
        var navigator = new FakeNavigator(_ => HealthySample());
        var minted = new List<DshWebUrl>();
        var logs = new List<string>();

        await Create(navigator, minted, logs, s_proxyUrl).RunAsync(Url(), CancellationToken.None);

        Assert.Single(minted);
        Assert.Empty(navigator.Navigated);
        Assert.Equal(1, navigator.ProbeCount);
        Assert.Contains(logs, line => line.Contains("页面裁决=healthy", StringComparison.Ordinal));
    }

    /// <summary>鉴权页：重铸并重载代理页一次、二次裁决仍 auth 只留痕（不循环、不挡启动）。</summary>
    [Fact]
    public async Task AuthPage_RemintsAndReloadsProxyOnce()
    {
        var navigator = new FakeNavigator(_ => AuthSample());
        var minted = new List<DshWebUrl>();
        var logs = new List<string>();

        await Create(navigator, minted, logs, s_proxyUrl).RunAsync(Url(), CancellationToken.None);

        Assert.Equal(2, minted.Count);
        Assert.Equal([s_proxyUrl], navigator.Navigated);
        Assert.Contains(logs, line => line.Contains("终页鉴权，重铸并重载代理页面（第 1 次）", StringComparison.Ordinal));
        Assert.Contains(
            logs,
            line => line.Contains("页面裁决=auth", StringComparison.Ordinal) && line.Contains("重铸后", StringComparison.Ordinal));
    }

    /// <summary>探针耗尽（超时）：按 unknown 放过启动，不导航不重铸。</summary>
    [Fact]
    public async Task ProbeExhausted_ContinuesAsUnknown()
    {
        var minted = new List<DshWebUrl>();
        var logs = new List<string>();

        await Create(new HangingProbeNavigator(), minted, logs, s_proxyUrl).RunAsync(Url(), CancellationToken.None);

        Assert.Single(minted);
        Assert.Contains(logs, line => line.Contains("鉴权探针超时", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("页面裁决=unknown", StringComparison.Ordinal));
    }

    /// <summary>等窗超时：铸币已先行、跳过本次导航与探针（holder 自 reload 即进）。</summary>
    [Fact]
    public async Task WindowNeverReady_TimesOut_SkipsSettle()
    {
        var navigator = new FakeNavigator(_ => HealthySample()) { WindowReady = false };
        var minted = new List<DshWebUrl>();
        var logs = new List<string>();

        await Create(navigator, minted, logs, s_proxyUrl).RunAsync(Url(), CancellationToken.None);

        Assert.Single(minted);
        Assert.Equal(0, navigator.ProbeCount);
        Assert.Contains(logs, line => line.Contains("等窗口可用超时", StringComparison.Ordinal));
    }

    /// <summary>导航调用与提交等待双双超时：各按「已提交」降级留痕并继续其后探针（两条降级分支的回归网）。</summary>
    [Fact]
    public async Task NavigateAndCommitTimeouts_DegradeAndContinue()
    {
        var navigator = new HangingNavigateNavigator(AuthSample());
        var minted = new List<DshWebUrl>();
        var logs = new List<string>();

        await Create(navigator, minted, logs, s_proxyUrl, navCallSeconds: 1).RunAsync(Url(), CancellationToken.None);

        Assert.Equal(2, minted.Count);
        Assert.Equal([s_proxyUrl], navigator.Navigated);
        Assert.Contains(logs, line => line.Contains("导航调用超时（1s），按已提交继续", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("导航提交等待超时（1s），按已提交继续", StringComparison.Ordinal));
    }

    /// <summary>应用退出（取消）：等待链上的取消必须上抛（R2 B1），不得吞成降级继续。</summary>
    [Fact]
    public async Task CancelledExit_Propagates()
    {
        var navigator = new FakeNavigator { WindowReady = false };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(navigator, [], [], s_proxyUrl).RunAsync(Url(), cts.Token));
    }

    /// <summary>构造用例（时限取最小可测值；测试不依赖真实等待）。</summary>
    /// <param name="navigator">fake 导航端口。</param>
    /// <param name="minted">铸币记录（按调用序）。</param>
    /// <param name="logs">日志记录。</param>
    /// <param name="proxyUrl">回环代理源；null = 绑定失败。</param>
    /// <param name="navCallSeconds">单次导航调用时限（默认 5s；超时分支的用例传 1s 压时限）。</param>
    /// <returns>用例实例。</returns>
    private static MainUiEntry Create(
        IWebViewNavigator navigator, List<DshWebUrl> minted, List<string> logs, Uri? proxyUrl, int navCallSeconds = 5) =>
        new(
            navigator,
            (url, log, ct) =>
            {
                minted.Add(url);
                return Task.FromResult(true);
            },
            proxyUrl,
            new MainUiEntryOptions(
                WindowReadyTimeoutSeconds: 1,
                WindowReadyPollIntervalSeconds: 1,
                AuthProbeTimeoutSeconds: 1,
                AuthProbeAttempts: 1,
                NavCallTimeoutSeconds: navCallSeconds,
                NavCommitTimeoutSeconds: 1),
            "probe-script",
            logs.Add);

    /// <summary>dsh 端点（铸币路径的入参）。</summary>
    /// <returns>branded 端点。</returns>
    private static DshWebUrl Url() => DshWebUrl.From(new Uri("http://127.0.0.1:9/?token=t"));
}
