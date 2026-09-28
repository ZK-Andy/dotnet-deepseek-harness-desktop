namespace DeepSeek.Harness.Desktop.Core.Tests.Update;

/// <summary>
/// 自更新协调器编排策略回归（ADR update-coordinator-core-port）：dev 门禁、对账清扫时机、
/// 安装路径事件序（SHA 复取→拉起→批准→关窗→兜底强退）、ready 横幅去重与后台检查异常收口。
/// 栈协作与 UI 交接全假件——协调器只依赖 Core 端口与委托，纯逻辑可单测。
/// </summary>
public class UpdateCoordinatorTests
{
    private static readonly ReleaseMeta s_meta =
        new("9.9.9", "app_9.9.9_linux-amd64.deb", "https://github.com/o/r/releases/download/9.9.9/a.deb", null);

    private sealed class FakePersistence : UpdateStateMachine.IPersistence
    {
        public UpdateStateMachine.ReadyRecord? Record { get; set; }
        public bool ThrowOnGet { get; set; }

        public Task<UpdateStateMachine.ReadyRecord?> GetAsync(CancellationToken ct) =>
            ThrowOnGet ? throw new InvalidOperationException("persist broken") : Task.FromResult(Record);

        public Task SetAsync(UpdateStateMachine.ReadyRecord record, CancellationToken ct)
        {
            Record = record;
            return Task.CompletedTask;
        }

        public Task<bool> AssetExistsAsync(string assetPath, CancellationToken ct) => Task.FromResult(true);

        public Task ClearAsync(CancellationToken ct)
        {
            Record = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEnvironment : IUpdateEnvironment
    {
        public bool Enabled { get; set; } = true;
        public UpdateOptions Options { get; set; } = new();
        public string UpdatesDir { get; set; } = "/tmp/updates";
        public string Version { get; set; } = "0.5.8";
        public string Rid { get; set; } = "linux-x64";
        public string? PackageKind { get; set; } = "deb";
        public (string UpdatesDir, string CurrentVersion)? Pruned { get; private set; }
        public FakePersistence Persistence { get; } = new();

        public bool IsEnabled(bool isDev) => Enabled;

        public UpdateOptions LoadOptions() => Options;

        public string CurrentVersion() => Version;

        public string ResolveUpdatesDir(string updatesDirName) => UpdatesDir;

        public string UpdateRid() => Rid;

        public string? DetectPackageKind() => PackageKind;

        public void PruneStale(string updatesDir, string currentVersion) => Pruned = (updatesDir, currentVersion);

        public UpdateStateMachine.IPersistence CreateReadyPersistence(string updatesDir) => Persistence;
    }

    private sealed class FakeFeed : IReleaseFeed
    {
        public ReleaseMeta? Latest { get; set; } = s_meta;
        public (UpdateOptions Options, string Rid, string? PackageKind)? Fetched { get; private set; }

        public Task<ReleaseMeta?> FetchLatestAsync(UpdateOptions options, string rid, string? packageKind, CancellationToken ct)
        {
            Fetched = (options, rid, packageKind);
            return Task.FromResult(Latest);
        }
    }

    private sealed class FakeDownloader : IPackageDownloader
    {
        private readonly List<string>? _events;

        public FakeDownloader(List<string>? events = null) => _events = events;

        public (string DestDir, TimeSpan Timeout)? Downloaded { get; private set; }
        public (string Repository, string Version, string AssetName)? ShaFetched { get; private set; }

        public Task<string> DownloadAsync(ReleaseMeta meta, string destDir, TimeSpan timeout, CancellationToken ct)
        {
            _events?.Add("download");
            Downloaded = (destDir, timeout);
            return Task.FromResult($"/tmp/updates/{meta.AssetName}");
        }

        public Task<string> FetchExpectedSha256Async(string repository, string version, string assetName, CancellationToken ct)
        {
            _events?.Add("sha");
            ShaFetched = (repository, version, assetName);
            return Task.FromResult("deadbeef");
        }
    }

    private sealed class FakeInstaller : IPackageInstaller
    {
        private readonly List<string>? _events;

        public FakeInstaller(List<string>? events = null) => _events = events;

        public (string AssetPath, string WorkDir, string Sha256, TimeSpan ObserveWindow)? Launched { get; private set; }
        public Exception? ThrowOnLaunch { get; set; }

        public Task LaunchAsync(string assetPath, string workDir, string expectedSha256, TimeSpan observeWindow, CancellationToken ct)
        {
            _events?.Add("launch");
            Launched = (assetPath, workDir, expectedSha256, observeWindow);
            if (ThrowOnLaunch is not null)
            {
                throw ThrowOnLaunch;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>UI 交接与日志记录器（事件序断言的家）。</summary>
    private sealed class Recorder
    {
        public List<string> Events { get; } = [];
        public List<UpdateState> Pushed { get; } = [];
        public List<(string Version, CancellationToken Token)> Banners { get; } = [];
        public List<string> Logs { get; } = [];
    }

    /// <summary>装配产物：协调器与假件/记录器引用集（显式类型消费，IDE0008 口径）。</summary>
    private sealed class Fixture
    {
        public required UpdateCoordinator Coordinator { get; init; }
        public required FakeEnvironment Env { get; init; }
        public required FakeFeed Feed { get; init; }
        public required FakeDownloader Downloader { get; init; }
        public required FakeInstaller Installer { get; init; }
        public required Recorder Rec { get; init; }
    }

    private static Fixture Build(bool isDev = false)
    {
        var rec = new Recorder();
        var env = new FakeEnvironment();
        var feed = new FakeFeed();
        var downloader = new FakeDownloader(rec.Events);
        var installer = new FakeInstaller(rec.Events);
        var cts = new CancellationTokenSource();
        var coordinator = new UpdateCoordinator(
            isDev,
            env,
            feed,
            downloader,
            installer,
            supervisorToken: () => cts.Token,
            pushState: rec.Pushed.Add,
            showReadyBanner: (version, token) =>
            {
                rec.Banners.Add((version, token));
                return Task.CompletedTask;
            },
            approveExit: () => rec.Events.Add("approve"),
            closeWindow: () => rec.Events.Add("close"),
            scheduleExitFallback: _ => rec.Events.Add("fallback"),
            log: rec.Logs.Add);
        return new Fixture
        {
            Coordinator = coordinator,
            Env = env,
            Feed = feed,
            Downloader = downloader,
            Installer = installer,
            Rec = rec,
        };
    }

    private static UpdateStateMachine Machine(UpdateCoordinator coordinator) =>
        coordinator.Machine ?? throw new InvalidOperationException("machine not loaded");

    /// <summary>dev 门禁不装载：机器为 null、不对账清扫、留痕提示。</summary>
    [Fact]
    public void Load_DevGateDisabled_DoesNotWireMachineAndLogs()
    {
        Fixture fx = Build(isDev: true);
        fx.Env.Enabled = false;

        fx.Coordinator.Load();

        Assert.Null(fx.Coordinator.Machine);
        Assert.Null(fx.Env.Pruned);
        Assert.Contains(fx.Rec.Logs, line => line.Contains("dev 运行时不装载"));
    }

    /// <summary>装载即对账清扫（目录+当前版本）并留痕上下文行。</summary>
    [Fact]
    public void Load_Enabled_WiresMachinePrunesStaleAndLogsContext()
    {
        Fixture fx = Build();

        fx.Coordinator.Load();

        Assert.NotNull(fx.Coordinator.Machine);
        Assert.Equal(("/tmp/updates", "0.5.8"), fx.Env.Pruned);
        Assert.Contains(fx.Rec.Logs, line => line.Contains("[host] 自更新：当前版本") && line.Contains("/tmp/updates"));
    }

    /// <summary>检查链路：feed 收 RID/包类型，下载收目录与配置超时，终点 ready。</summary>
    [Fact]
    public async Task Check_FetchesFeedWithRidAndKindAndDownloadsWithOptions()
    {
        Fixture fx = Build();
        fx.Coordinator.Load();

        await Machine(fx.Coordinator).CheckAsync(CancellationToken.None);

        Assert.NotNull(fx.Feed.Fetched);
        Assert.Equal((fx.Env.Options, fx.Env.Rid, fx.Env.PackageKind), fx.Feed.Fetched);
        Assert.NotNull(fx.Downloader.Downloaded);
        Assert.Equal("/tmp/updates", fx.Downloader.Downloaded!.Value.DestDir);
        Assert.Equal(TimeSpan.FromMinutes(fx.Env.Options.DownloadTimeoutMinutes), fx.Downloader.Downloaded!.Value.Timeout);
        Assert.Equal(UpdateStatus.Ready, Machine(fx.Coordinator).State.Status);
        Assert.Contains(fx.Rec.Pushed, state => state.Status == UpdateStatus.Ready && state.Version == "9.9.9");
    }

    /// <summary>安装路径事件序契约：SHA 复取→拉起→批准→关窗→兜底强退。</summary>
    [Fact]
    public async Task Install_OrderIsShaFetchLaunchApproveCloseFallback()
    {
        Fixture fx = Build();
        fx.Coordinator.Load();
        await Machine(fx.Coordinator).CheckAsync(CancellationToken.None);
        fx.Rec.Events.Clear();

        await Machine(fx.Coordinator).InstallAsync(CancellationToken.None);

        Assert.Equal(["sha", "launch", "approve", "close", "fallback"], fx.Rec.Events);
        // SHA 复取与拉起的判据都来自配置与状态机：仓库/版本/资产名、期望哈希、观察窗时长。
        Assert.Equal((fx.Env.Options.Repository, "9.9.9", "app_9.9.9_linux-amd64.deb"), fx.Downloader.ShaFetched);
        Assert.Equal("/tmp/updates/app_9.9.9_linux-amd64.deb", fx.Installer.Launched!.Value.AssetPath);
        Assert.Equal("/tmp/updates", fx.Installer.Launched!.Value.WorkDir);
        Assert.Equal("deadbeef", fx.Installer.Launched!.Value.Sha256);
        Assert.Equal(TimeSpan.FromSeconds(fx.Env.Options.PkexecObserveSeconds), fx.Installer.Launched!.Value.ObserveWindow);
    }

    /// <summary>拉起失败：状态机回退 ready，批准/关窗/兜底不触发。</summary>
    [Fact]
    public async Task Install_LaunchFails_RevertsToReadyWithoutUiHandoff()
    {
        Fixture fx = Build();
        fx.Installer.ThrowOnLaunch = new InvalidOperationException("pkexec denied");
        fx.Coordinator.Load();
        await Machine(fx.Coordinator).CheckAsync(CancellationToken.None);
        fx.Rec.Events.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Machine(fx.Coordinator).InstallAsync(CancellationToken.None));

        Assert.Equal(UpdateStatus.Ready, Machine(fx.Coordinator).State.Status);
        Assert.Equal(["sha", "launch"], fx.Rec.Events);
    }

    /// <summary>ready 横幅去重：失败回 ready 再触发仍只弹一次。</summary>
    [Fact]
    public async Task Start_ReadyShownOnce_AcrossReadyTransitions()
    {
        Fixture fx = Build();
        fx.Installer.ThrowOnLaunch = new InvalidOperationException("pkexec denied");
        fx.Env.Persistence.Record = new UpdateStateMachine.ReadyRecord("9.9.9", "/tmp/updates/a.deb");
        fx.Coordinator.Load();
        fx.Coordinator.Start();

        await WaitUntilAsync(() => fx.Rec.Banners.Count == 1);
        Assert.Equal("9.9.9", fx.Rec.Banners[0].Version);

        // 安装失败回退 ready：订阅者再触发，横幅去重仍只弹一次。
        await Assert.ThrowsAsync<InvalidOperationException>(() => Machine(fx.Coordinator).InstallAsync(CancellationToken.None));
        Assert.Equal(UpdateStatus.Ready, Machine(fx.Coordinator).State.Status);
        Assert.Single(fx.Rec.Banners);
    }

    /// <summary>后台检查持久化异常：留痕收口不崩。</summary>
    [Fact]
    public async Task Start_PersistenceBroken_LogsBackgroundFailure()
    {
        Fixture fx = Build();
        fx.Env.Persistence.ThrowOnGet = true;
        fx.Coordinator.Load();
        fx.Coordinator.Start();

        await WaitUntilAsync(() => fx.Rec.Logs.Any(line => line.Contains("[update] start 失败")));
        Assert.Contains(fx.Rec.Logs, line => line.Contains("persist broken"));
    }

    /// <summary>后台 Task.Run 的有界轮询等待（不引入固定 sleep 的时序收敛点）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met within wait window");
    }
}
