using DeepSeek.Harness.Desktop.Core.Recovery;
using DeepSeek.Harness.Desktop.Core.Ui;

namespace DeepSeek.Harness.Desktop.Core.Tests.Bootstrap;

/// <summary>运行时监督用例（ADR 组合根机制收官 step-1 的 B2）：引导落定门控、子进程退出后的失败面承载与
/// 恢复重验、残留锁死跳过重启、回落承载面的收口导航，经 fake 宿主/承载面/铸币态驱动。</summary>
public class RuntimeSupervisionTests
{
    private static readonly Uri s_proxyUrl = new("http://127.0.0.1:5555/");

    /// <summary>fake 宿主：前 N 次等退出立即完成（每次 = 一次子进程退出），之后挂起（监督循环停驻不空转）。</summary>
    private sealed class FakeSupervisionHost : IRuntimeHost
    {
        private int _exitWaits;

        public IReadOnlyList<string> StderrTail => Array.Empty<string>();

        public string RuntimeDescription => "fake dsh";

        public bool Residue { get; init; }

        public int ExitSignals { get; init; } = 1;

        public Uri? NextUrl { get; init; }

        public int RestartCalls { get; private set; }

        public int ExitWaits => _exitWaits;

        public bool TryDetectUnreapableResidue() => Residue;

        public Task<Uri?> StartAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult<Uri?>(null);

        public Task<Uri?> RestartAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            RestartCalls++;
            return Task.FromResult(NextUrl);
        }

        public Task WaitForExitAsync()
        {
            if (_exitWaits++ < ExitSignals)
            {
                return Task.CompletedTask;
            }

            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
    }

    /// <summary>fake 引导服务：落定可切（false 覆盖「引导未落定不进监视」门控）。</summary>
    private sealed class FakeBootstrap : IFirstBootBootstrap
    {
        public bool Settles { get; init; } = true;

        public bool IsNeeded => false;

        public string? ResolvedDshVersion => null;

        public RuntimeBootstrapGate Gate => new();

        public void Resolve()
        {
        }

        public void RegisterCliShim()
        {
        }

        public void Start(Func<DshWebUrl, CancellationToken, Task> onRuntimeReady)
        {
        }

        public void Cancel()
        {
        }

        public Task<bool> WaitSettledAsync(TimeSpan? timeout, CancellationToken ct) => Task.FromResult(Settles);
    }

    /// <summary>fake 失败面：两条通道的调用与原因留存 + 首次展示信号。</summary>
    private sealed class FakeSurface : IRecoverySurface
    {
        public bool HasNativeChannel { get; init; } = true;

        public bool NativeThrows { get; init; }

        public bool NativeHangs { get; init; }

        public List<string> NativeReasons { get; } = [];

        public List<string> PageReasons { get; } = [];

        public TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ShowNativeAsync(string reason, CancellationToken ct)
        {
            NativeReasons.Add(reason);
            Shown.TrySetResult();
            if (NativeThrows)
            {
                throw new InvalidOperationException("原生通道故障");
            }

            return NativeHangs
                ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task
                : Task.CompletedTask;
        }

        public ValueTask ShowPageAsync(string reason)
        {
            PageReasons.Add(reason);
            Shown.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>fake 铸币态：失效计数 + 重验信号（携端点）。</summary>
    private sealed class FakeMintEpoch : IMintEpoch
    {
        private int _revalidations;

        public int Invalidations { get; private set; }

        /// <summary>第二次重验信号（覆盖「两轮退出 + 恢复」场景）。</summary>
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<DshWebUrl> Revalidated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Invalidate()
        {
            Invalidations++;
            return true;
        }

        public Task RevalidateAsync(DshWebUrl url, CancellationToken ct)
        {
            Revalidated.TrySetResult(url);
            if (Interlocked.Increment(ref _revalidations) >= 2)
            {
                Second.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>fake 导航端口：收口导航信号。</summary>
    private sealed class FakeNavigator : IWebViewNavigator
    {
        public bool IsWindowReady => true;

        public TaskCompletionSource<Uri> Navigated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetArrivalCallback(Action onArrived)
        {
        }

        public Task NavigateAsync(Uri target, CancellationToken ct)
        {
            Navigated.TrySetResult(target);
            return Task.CompletedTask;
        }

        public Task<string?> EvaluateScriptAsync(string script) => Task.FromResult<string?>(null);
    }

    /// <summary>引导未落定：监督循环不进入（不空转进恢复循环、不覆写引导页）。</summary>
    [Fact]
    public async Task SettleNotReached_SkipsSupervisionLoop()
    {
        var host = new FakeSupervisionHost { ExitSignals = 0 };
        var surface = new FakeSurface();
        var mint = new FakeMintEpoch();

        await Create(new FakeBootstrap { Settles = false }, surface, mint, new FakeNavigator())
            .RunAsync(host, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, host.ExitWaits);
        Assert.Empty(surface.NativeReasons);
        Assert.Equal(0, mint.Invalidations);
    }

    /// <summary>子进程退出（原生通道）：先失效铸币态并弹失败面（崩溃原因），重启后重验铸币态；
    /// 原生面不碰 DOM，故零壳导航。</summary>
    [Fact]
    public async Task ChildExit_NativeSurface_InvalidatesShowsRevalidatesWithNoNavigation()
    {
        var host = new FakeSupervisionHost { ExitSignals = 1, NextUrl = new Uri("http://127.0.0.1:6666/?token=t") };
        var surface = new FakeSurface();
        var mint = new FakeMintEpoch();
        var navigator = new FakeNavigator();
        using var cts = new CancellationTokenSource();

        Task task = Create(new FakeBootstrap(), surface, mint, navigator, stuckThreshold: 1).RunAsync(host, cts.Token);
        DshWebUrl revalidated = await mint.Revalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("http://127.0.0.1:6666", revalidated.Authority);
        Assert.Equal(1, mint.Invalidations);
        Assert.Equal([UiCopy.ReasonRuntimeCrashed(false)], surface.NativeReasons);
        Assert.Empty(surface.PageReasons);
        Assert.False(navigator.Navigated.Task.IsCompleted);

        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>残留锁死：失败面带锁原因、跳过本轮重启（不重验、不导航）。</summary>
    [Fact]
    public async Task ResidueLock_ShowsLockReason_SkipsRestart()
    {
        var host = new FakeSupervisionHost { Residue = true, ExitSignals = 1 };
        var surface = new FakeSurface();
        var mint = new FakeMintEpoch();
        using var cts = new CancellationTokenSource();

        Task task = Create(new FakeBootstrap(), surface, mint, new FakeNavigator(), stuckThreshold: 1).RunAsync(host, cts.Token);
        await surface.Shown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([UiCopy.ReasonDshResidueLocked(false)], surface.NativeReasons);
        Assert.Equal(0, host.RestartCalls);
        Assert.False(mint.Revalidated.Task.IsCompleted);

        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>回落承载面（无原生通道）：失败面走页内注入，恢复后补一次收口导航回代理根。</summary>
    [Fact]
    public async Task PageFallback_AfterRecovery_NavigatesBackToProxyRoot()
    {
        var host = new FakeSupervisionHost { ExitSignals = 1, NextUrl = new Uri("http://127.0.0.1:6666/?token=t") };
        var surface = new FakeSurface { HasNativeChannel = false };
        var mint = new FakeMintEpoch();
        var navigator = new FakeNavigator();
        using var cts = new CancellationTokenSource();

        Task task = Create(new FakeBootstrap(), surface, mint, navigator, stuckThreshold: 1).RunAsync(host, cts.Token);
        Uri target = await navigator.Navigated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(s_proxyUrl, target);
        Assert.Equal([UiCopy.ReasonRuntimeCrashed(false)], surface.PageReasons);
        Assert.Empty(surface.NativeReasons);
        await mint.Revalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>原生承载故障：回落页内恢复面（失败面绝不静默消失），并因覆写 DOM 在恢复后补收口导航。</summary>
    [Fact]
    public async Task NativeChannelFailure_FallsBackToPageSurface()
    {
        var host = new FakeSupervisionHost { ExitSignals = 1, NextUrl = new Uri("http://127.0.0.1:6666/?token=t") };
        var surface = new FakeSurface { NativeThrows = true };
        var mint = new FakeMintEpoch();
        var navigator = new FakeNavigator();
        using var cts = new CancellationTokenSource();

        Task task = Create(new FakeBootstrap(), surface, mint, navigator, stuckThreshold: 1).RunAsync(host, cts.Token);
        Uri target = await navigator.Navigated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(s_proxyUrl, target);
        Assert.Equal([UiCopy.ReasonRuntimeCrashed(false)], surface.PageReasons);

        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>「模态在飞」守卫：首个原生模态未处置时不叠第二个——无恢复的连败下第 3 轮弹一次在飞模态，
    /// 第 4 轮被守卫挡住（删守卫即红）。</summary>
    [Fact]
    public async Task NativeModalInFlight_BlocksSecondModalAcrossRecoveryCycles()
    {
        var host = new FakeSupervisionHost { ExitSignals = 4 };
        var surface = new FakeSurface { NativeHangs = true };
        var mint = new FakeMintEpoch();
        using var cts = new CancellationTokenSource();

        Task task = Create(new FakeBootstrap(), surface, mint, new FakeNavigator()).RunAsync(host, cts.Token);
        await surface.Shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRestartsAsync(host, 4);

        Assert.Single(surface.NativeReasons);
        Assert.Equal(4, host.RestartCalls);

        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>卡住阈值：前两次连续崩溃只留痕不弹面，第 3 次才弹一次（偶发崩溃零打扰，退出逃生口只留给真卡住；
    /// 被门住的前两轮重启照跑，门只管面不管恢复）。</summary>
    [Fact]
    public async Task StuckThreshold_SurfacesOnlyOnThirdConsecutiveCrash()
    {
        var host = new FakeSupervisionHost { ExitSignals = 3 };
        var surface = new FakeSurface();
        var mint = new FakeMintEpoch();
        using var cts = new CancellationTokenSource();

        Task task = Create(new FakeBootstrap(), surface, mint, new FakeNavigator()).RunAsync(host, cts.Token);
        await surface.Shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRestartsAsync(host, 3);

        Assert.Single(surface.NativeReasons);
        Assert.Equal(3, host.RestartCalls);

        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>恢复成功复位计数：崩溃→恢复→崩溃→恢复永不攒到阈值（2026-10-15 实机形态：14 秒自愈，不该弹面）。</summary>
    [Fact]
    public async Task RecoveryResetsStreak_NeverSurfacesWhenEachCrashRecovers()
    {
        var host = new FakeSupervisionHost { ExitSignals = 2, NextUrl = new Uri("http://127.0.0.1:6666/?token=t") };
        var surface = new FakeSurface();
        var mint = new FakeMintEpoch();
        using var cts = new CancellationTokenSource();

        Task task = Create(new FakeBootstrap(), surface, mint, new FakeNavigator()).RunAsync(host, cts.Token);
        await mint.Second.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(surface.NativeReasons);
        Assert.Empty(surface.PageReasons);
        Assert.Equal(2, host.RestartCalls);

        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>等监督循环完成指定次数重启（门住周期重启照跑不断言竞态：5 秒 cap 内未达即测试失败）。</summary>
    /// <param name="host">fake 宿主（重启计数）。</param>
    /// <param name="restarts">期望重启次数。</param>
    private static async Task WaitForRestartsAsync(FakeSupervisionHost host, int restarts)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.RestartCalls < restarts)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"监督循环未在 5 秒内完成 {restarts} 次重启（实际 {host.RestartCalls} 次）");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>构造监督用例（时限取最小可测值；语言固定中文分支，断言文案可确定）。</summary>
    /// <param name="bootstrap">fake 引导服务。</param>
    /// <param name="surface">fake 失败面。</param>
    /// <param name="mint">fake 铸币态。</param>
    /// <param name="navigator">fake 导航端口。</param>
    /// <param name="stuckThreshold">卡住阈值（缺省 3 与生产默认对齐；传 1 即每次崩溃都弹面，保旧语义用例）。</param>
    /// <returns>用例实例。</returns>
    private static RuntimeSupervision Create(
        IFirstBootBootstrap bootstrap, IRecoverySurface surface, IMintEpoch mint, IWebViewNavigator navigator,
        int stuckThreshold = 3)
    {
        var locale = new UiLocale();
        locale.Set("zh-CN");
        return new RuntimeSupervision(
            bootstrap, surface, mint, navigator, s_proxyUrl, locale, new RuntimeSupervisionOptions(1, 1, 1, 60, stuckThreshold));
    }
}
