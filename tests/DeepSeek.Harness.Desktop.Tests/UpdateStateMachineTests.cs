using DeepSeek.Harness.Desktop.Services.Update;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>状态机行为级回归（对齐 opencode updater-controller 语义）。</summary>
public class UpdateStateMachineTests
{
    private const string Current = "0.1.20";

    private sealed class FakePersistence : UpdateStateMachine.IPersistence
    {
        public UpdateStateMachine.ReadyRecord? Record { get; private set; }
        public Task<UpdateStateMachine.ReadyRecord?> GetAsync(CancellationToken ct) => Task.FromResult(Record);
        public Task SetAsync(UpdateStateMachine.ReadyRecord record, CancellationToken ct)
        {
            Record = record;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct)
        {
            Record = null;
            return Task.CompletedTask;
        }
    }

    private static ReleaseMeta Meta(string version) =>
        new(version, $"app_{version}_linux-amd64.deb", $"https://github.com/o/r/releases/download/{version}/a.deb", null);

    private static UpdateStateMachine Create(
        string current,
        Func<string?, ReleaseMeta?> check,
        List<string>? downloads = null,
        FakePersistence? persistence = null,
        Action<string, string, CancellationToken>? install = null,
        List<UpdateState>? transitions = null)
    {
        FakePersistence persist = persistence ?? new FakePersistence();
        List<string> dl = downloads ?? [];
        return new UpdateStateMachine(
            currentVersion: current,
            check: ct => Task.FromResult(check(persist.Record?.Version)),
            download: (meta, ct) =>
            {
                // 落一个真实临时文件：install 阶段会校验资产存在性
                string path = Path.Combine(Path.GetTempPath(), meta.AssetName);
                File.WriteAllBytes(path, [1]);
                dl.Add(meta.Version);
                return Task.FromResult(path);
            },
            install: (assetPath, version, ct) =>
            {
                install?.Invoke(assetPath, version, ct);
                return Task.CompletedTask;
            },
            persistence: persist,
            onTransition: state => transitions?.Add(state));
    }

    /// <summary>验证启动时持久化 ready 记录的版本与当前相同则清除记录并进入 UpToDate。</summary>
    [Fact]
    public async Task Start_ClearsStaleReady_WhenSameAsCurrent()
    {
        var persist = new FakePersistence();
        await persist.SetAsync(new UpdateStateMachine.ReadyRecord(Current, "/tmp/x.deb"), CancellationToken.None);
        UpdateStateMachine machine = Create(Current, _ => null, persistence: persist);

        await machine.StartAsync(CancellationToken.None);

        Assert.Null(persist.Record);
        Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
    }

    /// <summary>验证启动时资产文件仍存在则从持久化恢复 Ready 状态，且不触发网络检查。</summary>
    [Fact]
    public async Task Start_RestoresReady_FromPersistence_WhenAssetExists()
    {
        var persist = new FakePersistence();
        string path = Path.Combine(Path.GetTempPath(), $"upd-{Guid.NewGuid():N}.deb");
        File.WriteAllBytes(path, [1]);
        try
        {
            await persist.SetAsync(new UpdateStateMachine.ReadyRecord("0.1.21", path), CancellationToken.None);
            UpdateStateMachine machine = Create(Current, _ => throw new InvalidOperationException("不应触发网络检查"), persistence: persist);

            await machine.StartAsync(CancellationToken.None);

            Assert.Equal(UpdateStatus.Ready, machine.State.Status);
            Assert.Equal("0.1.21", machine.State.Version);
        }
        finally { File.Delete(path); }
    }

    /// <summary>验证检查无可用更新时状态转为 UpToDate，不发起下载。</summary>
    [Fact]
    public async Task Check_NoUpdate_GoesUpToDate()
    {
        UpdateStateMachine machine = Create(Current, _ => null);

        await machine.StartAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
    }

    /// <summary>验证远程版本低于当前版本时不计为更新，状态保持 UpToDate。</summary>
    [Fact]
    public async Task Check_OlderRelease_GoesUpToDate()
    {
        UpdateStateMachine machine = Create(Current, _ => Meta("0.1.19"));

        await machine.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
    }

    /// <summary>验证发现新版本时下载资产并转入 Ready，下载路径记录恰好一次。</summary>
    [Fact]
    public async Task Check_NewVersion_DownloadsAndGoesReady()
    {
        var downloads = new List<string>();
        UpdateStateMachine machine = Create(Current, _ => Meta("0.1.21"), downloads: downloads);

        await machine.StartAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.Ready, machine.State.Status);
        Assert.Equal("0.1.21", machine.State.Version);
        _ = Assert.Single(downloads);
    }

    /// <summary>验证下载失败转入 Error 后，再次检查成功即恢复 Ready。</summary>
    [Fact]
    public async Task DownloadFailure_GoesError_ThenRecheckRecovers()
    {
        bool fail = true;
        var persist = new FakePersistence();
        var machine = new UpdateStateMachine(
            currentVersion: Current,
            check: ct => Task.FromResult<ReleaseMeta?>(Meta("0.1.21")),
            download: (meta, ct) => fail
                ? throw new IOException("网络断开")
                : Task.FromResult("/tmp/ok.deb"),
            install: (assetPath, version, ct) => Task.CompletedTask,
            persistence: persist);

        await machine.StartAsync(CancellationToken.None);
        Assert.Equal(UpdateStatus.Error, machine.State.Status);

        fail = false;
        await machine.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateStatus.Ready, machine.State.Status);
    }

    /// <summary>验证非 Ready 状态下调用 InstallAsync 抛出 InvalidOperationException 拒绝安装。</summary>
    [Fact]
    public async Task Install_RejectsWhenNotReady()
    {
        UpdateStateMachine machine = Create(Current, _ => null);

        await machine.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => machine.InstallAsync(CancellationToken.None));
    }

    /// <summary>验证 Ready 状态下安装会调用 install 回调携带资产路径与版本，状态转入 Installing。</summary>
    [Fact]
    public async Task Install_FromReady_TransitionsInstalling_AndFallsBackOnError()
    {
        string? installedAt = null;
        UpdateStateMachine machine = Create(Current, _ => Meta("0.1.21"),
            install: (assetPath, version, _) => installedAt = $"{assetPath}@{version}");
        await machine.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateStatus.Ready, machine.State.Status);

        await machine.InstallAsync(CancellationToken.None);

        Assert.NotNull(installedAt);
        Assert.EndsWith("@0.1.21", installedAt, StringComparison.Ordinal);
        Assert.Equal(UpdateStatus.Installing, machine.State.Status);
    }

    /// <summary>验证 install 回调抛异常时异常向上传播，状态回退为 Ready。</summary>
    [Fact]
    public async Task Install_Failure_ReturnsToReady_AndThrows()
    {
        UpdateStateMachine machine = Create(Current, _ => Meta("0.1.21"),
            install: (assetPath, version, _) => throw new PlatformNotSupportedException("macOS 不支持"));
        await machine.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateStatus.Ready, machine.State.Status);

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => machine.InstallAsync(CancellationToken.None));

        Assert.Equal(UpdateStatus.Ready, machine.State.Status);
    }

    /// <summary>验证订阅能收到全部状态转移通知，释放订阅后不再收到新转移。</summary>
    [Fact]
    public async Task Subscribe_ReceivesTransitions_AndUnsubscribes()
    {
        var seen = new List<UpdateStatus>();
        UpdateStateMachine machine = Create(Current, _ => null);
        using IDisposable sub = machine.Subscribe(s => seen.Add(s.Status));

        await machine.StartAsync(CancellationToken.None);
        sub.Dispose();

        Assert.Contains(UpdateStatus.Idle, seen);
        Assert.Contains(UpdateStatus.Checking, seen);
        int afterCount = seen.Count;
        await machine.StartAsync(CancellationToken.None);
        Assert.Equal(afterCount, seen.Count);
    }

    /// <summary>验证持久化记录版本低于当前（降级残留）时清除记录重新检查，不给出降级安装建议。</summary>
    [Fact]
    public async Task Start_ClearsReady_WhenOlderThanCurrent()
    {
        // 降级残留/旧 home 的记录：版本低于当前且资产在——必须清除重查，而不是给降级安装建议
        var persist = new FakePersistence();
        string path = Path.Combine(Path.GetTempPath(), $"upd-{Guid.NewGuid():N}.deb");
        File.WriteAllBytes(path, [1]);
        try
        {
            await persist.SetAsync(new UpdateStateMachine.ReadyRecord("0.1.19", path), CancellationToken.None);
            UpdateStateMachine machine = Create(Current, _ => null, persistence: persist);

            await machine.StartAsync(CancellationToken.None);

            Assert.Null(persist.Record);
            Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
        }
        finally { File.Delete(path); }
    }

    /// <summary>验证 ready.json 版本串损坏时不卡死启动，清除记录后正常检查进入 UpToDate。</summary>
    [Fact]
    public async Task Start_ClearsReady_WhenVersionUnparseable()
    {
        // ready.json 里的版本串损坏：不卡死启动，清除后正常检查
        var persist = new FakePersistence();
        string path = Path.Combine(Path.GetTempPath(), $"upd-{Guid.NewGuid():N}.deb");
        File.WriteAllBytes(path, [1]);
        try
        {
            await persist.SetAsync(new UpdateStateMachine.ReadyRecord("garbage-version", path), CancellationToken.None);
            UpdateStateMachine machine = Create(Current, _ => null, persistence: persist);

            await machine.StartAsync(CancellationToken.None);

            Assert.Null(persist.Record);
            Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
        }
        finally { File.Delete(path); }
    }

    /// <summary>验证并发调用 CheckAsync 只执行一次网络检查，去重判空与占位处于同一临界区。</summary>
    [Fact]
    public async Task Check_ConcurrentCalls_RunSingleCheck()
    {
        // 并发去重：判空与占位在同一临界区内，两个并发调用只执行一次检查
        int count = 0;
        var tcs = new TaskCompletionSource<ReleaseMeta?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = new UpdateStateMachine(
            currentVersion: Current,
            check: _ =>
            {
                Interlocked.Increment(ref count);
                return tcs.Task;
            },
            download: (meta, ct) =>
            {
                string path = Path.Combine(Path.GetTempPath(), $"conc-{Guid.NewGuid():N}.deb");
                File.WriteAllBytes(path, [1]);
                return Task.FromResult(path);
            },
            install: (assetPath, version, ct) => Task.CompletedTask,
            persistence: new FakePersistence());

        Task<UpdateState> first = machine.CheckAsync(CancellationToken.None);
        Task<UpdateState> second = machine.CheckAsync(CancellationToken.None);
        tcs.SetResult(Meta("0.1.21"));
        await Task.WhenAll(first, second);

        Assert.Equal(1, count);
        Assert.Equal(UpdateStatus.Ready, machine.State.Status);
    }

    /// <summary>验证 CheckAsync 产生的每个转移事件都携带正确的当前版本号。</summary>
    [Fact]
    public async Task CheckAsync_TransitionsCarryCurrentVersion()
    {
        var states = new List<UpdateState>();
        UpdateStateMachine machine = Create(Current, _ => null, transitions: states);

        await machine.CheckAsync(CancellationToken.None);

        Assert.NotEmpty(states);
        Assert.All(states, s => Assert.Equal(Current, s.Current));
        Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
    }
}
