namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Bootstrap;

/// <summary>启动期告知服务：引导落定门控、版本底线横幅、脏退横幅三条判定（版本探测经假探针注入）。</summary>
public class StartupNoticeServiceTests
{
    /// <summary>验证引导落定闸门未放行（未落定或已取消）时不推任何横幅：横幅不抢跑在引导之前。</summary>
    [Fact]
    public async Task RunAsync_SettleGateNotPassed_PushesNoBanner()
    {
        var floors = new List<string>();
        var unclean = new List<int>();
        // 夹具必须「闸一放行就必推横幅」：版本探测返低于底线的值 + 上轮脏退为真——
        // 否则闸门不存在时两个集合也恒空，断言就成了恒绿，挡不住它声称要挡的回归。
        StartupNoticeService service = NewService(
            new FakeBootstrap(settled: false), floors, unclean, probe: "0.0.1", previousRunUnclean: true);

        await service.RunAsync(CancellationToken.None);

        Assert.Empty(floors);
        Assert.Empty(unclean);
    }

    /// <summary>验证探测到的版本低于底线时推版本底线横幅，并把版本号交给展示面。</summary>
    [Fact]
    public async Task RunAsync_VersionBelowFloor_PushesVersionFloorBanner()
    {
        var floors = new List<string>();
        var unclean = new List<int>();
        StartupNoticeService service = NewService(new FakeBootstrap(settled: true), floors, unclean, probe: "0.0.1");

        await service.RunAsync(CancellationToken.None);

        Assert.Equal("0.0.1", Assert.Single(floors));
        Assert.Empty(unclean);
    }

    /// <summary>验证版本探测失败（未知）时跳过底线横幅：未知 ≠ 不合格，不拿横幅打扰用户。</summary>
    [Fact]
    public async Task RunAsync_ProbeFailed_SkipsVersionFloorBanner()
    {
        var floors = new List<string>();
        var unclean = new List<int>();
        StartupNoticeService service = NewService(new FakeBootstrap(settled: true), floors, unclean, probe: null);

        await service.RunAsync(CancellationToken.None);

        Assert.Empty(floors);
    }

    /// <summary>验证上轮非受控退出时推脏退横幅（与版本底线横幅互不影响）。</summary>
    [Fact]
    public async Task RunAsync_PreviousRunUnclean_PushesUncleanExitBanner()
    {
        var floors = new List<string>();
        var unclean = new List<int>();
        StartupNoticeService service = NewService(
            new FakeBootstrap(settled: true), floors, unclean, probe: null, previousRunUnclean: true);

        await service.RunAsync(CancellationToken.None);

        Assert.Single(unclean);
        Assert.Empty(floors);
    }

    private static StartupNoticeService NewService(
        IFirstBootBootstrap bootstrap,
        List<string> floors,
        List<int> unclean,
        string? probe = null,
        bool previousRunUnclean = false) =>
        new(
            bootstrap,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            previousRunUnclean,
            _ => { },
            (version, _) =>
            {
                floors.Add(version);
                return Task.CompletedTask;
            },
            _ =>
            {
                unclean.Add(1);
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult(probe));

    private sealed class FakeBootstrap : IFirstBootBootstrap
    {
        private readonly bool _settled;

        public FakeBootstrap(bool settled) => _settled = settled;

        public bool IsNeeded => false;

        public RuntimeBootstrapGate Gate => new();

        public void Resolve() { }

        public void RegisterCliShim() { }

        public void Start(Func<DshWebUrl, CancellationToken, Task> onRuntimeReady) { }

        public void Cancel() { }

        public Task<bool> WaitSettledAsync(TimeSpan? timeout, CancellationToken ct) => Task.FromResult(_settled);
    }
}
