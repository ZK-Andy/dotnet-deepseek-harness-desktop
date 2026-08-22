using DeepSeek.Harness.Desktop.Services.Update;
using Xunit;

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
        var persist = persistence ?? new FakePersistence();
        var dl = downloads ?? [];
        return new UpdateStateMachine(
            currentVersion: current,
            check: ct => Task.FromResult(check(persist.Record?.Version)),
            download: (meta, ct) =>
            {
                // 落一个真实临时文件：install 阶段会校验资产存在性
                var path = Path.Combine(Path.GetTempPath(), meta.AssetName);
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

    [Fact]
    public async Task Start_ClearsStaleReady_WhenSameAsCurrent()
    {
        var persist = new FakePersistence();
        await persist.SetAsync(new UpdateStateMachine.ReadyRecord(Current, "/tmp/x.deb"), CancellationToken.None);
        var machine = Create(Current, _ => null, persistence: persist);

        await machine.StartAsync(CancellationToken.None);

        Assert.Null(persist.Record);
        Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
    }

    [Fact]
    public async Task Start_RestoresReady_FromPersistence_WhenAssetExists()
    {
        var persist = new FakePersistence();
        var path = Path.Combine(Path.GetTempPath(), $"upd-{Guid.NewGuid():N}.deb");
        File.WriteAllBytes(path, [1]);
        try
        {
            await persist.SetAsync(new UpdateStateMachine.ReadyRecord("0.1.21", path), CancellationToken.None);
            var machine = Create(Current, _ => throw new InvalidOperationException("不应触发网络检查"), persistence: persist);

            await machine.StartAsync(CancellationToken.None);

            Assert.Equal(UpdateStatus.Ready, machine.State.Status);
            Assert.Equal("0.1.21", machine.State.Version);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Check_NoUpdate_GoesUpToDate()
    {
        var machine = Create(Current, _ => null);

        await machine.StartAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
    }

    [Fact]
    public async Task Check_OlderRelease_GoesUpToDate()
    {
        var machine = Create(Current, _ => Meta("0.1.19"));

        await machine.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
    }

    [Fact]
    public async Task Check_NewVersion_DownloadsAndGoesReady()
    {
        var downloads = new List<string>();
        var machine = Create(Current, _ => Meta("0.1.21"), downloads: downloads);

        await machine.StartAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.Ready, machine.State.Status);
        Assert.Equal("0.1.21", machine.State.Version);
        _ = Assert.Single(downloads);
    }

    [Fact]
    public async Task DownloadFailure_GoesError_ThenRecheckRecovers()
    {
        var fail = true;
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

    [Fact]
    public async Task Install_RejectsWhenNotReady()
    {
        var machine = Create(Current, _ => null);

        await machine.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => machine.InstallAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Install_FromReady_TransitionsInstalling_AndFallsBackOnError()
    {
        string? installedAt = null;
        var machine = Create(Current, _ => Meta("0.1.21"),
            install: (assetPath, version, _) => installedAt = $"{assetPath}@{version}");
        await machine.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateStatus.Ready, machine.State.Status);

        await machine.InstallAsync(CancellationToken.None);

        Assert.NotNull(installedAt);
        Assert.EndsWith("@0.1.21", installedAt, StringComparison.Ordinal);
        Assert.Equal(UpdateStatus.Installing, machine.State.Status);
    }

    [Fact]
    public async Task Install_Failure_ReturnsToReady_AndThrows()
    {
        var machine = Create(Current, _ => Meta("0.1.21"),
            install: (assetPath, version, _) => throw new PlatformNotSupportedException("macOS 不支持"));
        await machine.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateStatus.Ready, machine.State.Status);

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => machine.InstallAsync(CancellationToken.None));

        Assert.Equal(UpdateStatus.Ready, machine.State.Status);
    }

    [Fact]
    public async Task Subscribe_ReceivesTransitions_AndUnsubscribes()
    {
        var seen = new List<UpdateStatus>();
        var machine = Create(Current, _ => null);
        using var sub = machine.Subscribe(s => seen.Add(s.Status));

        await machine.StartAsync(CancellationToken.None);
        sub.Dispose();

        Assert.Contains(UpdateStatus.Idle, seen);
        Assert.Contains(UpdateStatus.Checking, seen);
        var afterCount = seen.Count;
        await machine.StartAsync(CancellationToken.None);
        Assert.Equal(afterCount, seen.Count);
    }

    [Fact]
    public async Task Start_ClearsReady_WhenOlderThanCurrent()
    {
        // 降级残留/旧 home 的记录：版本低于当前且资产在——必须清除重查，而不是给降级安装建议
        var persist = new FakePersistence();
        var path = Path.Combine(Path.GetTempPath(), $"upd-{Guid.NewGuid():N}.deb");
        File.WriteAllBytes(path, [1]);
        try
        {
            await persist.SetAsync(new UpdateStateMachine.ReadyRecord("0.1.19", path), CancellationToken.None);
            var machine = Create(Current, _ => null, persistence: persist);

            await machine.StartAsync(CancellationToken.None);

            Assert.Null(persist.Record);
            Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Start_ClearsReady_WhenVersionUnparseable()
    {
        // ready.json 里的版本串损坏：不卡死启动，清除后正常检查
        var persist = new FakePersistence();
        var path = Path.Combine(Path.GetTempPath(), $"upd-{Guid.NewGuid():N}.deb");
        File.WriteAllBytes(path, [1]);
        try
        {
            await persist.SetAsync(new UpdateStateMachine.ReadyRecord("garbage-version", path), CancellationToken.None);
            var machine = Create(Current, _ => null, persistence: persist);

            await machine.StartAsync(CancellationToken.None);

            Assert.Null(persist.Record);
            Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Check_ConcurrentCalls_RunSingleCheck()
    {
        // 并发去重：判空与占位在同一临界区内，两个并发调用只执行一次检查
        var count = 0;
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
                var path = Path.Combine(Path.GetTempPath(), $"conc-{Guid.NewGuid():N}.deb");
                File.WriteAllBytes(path, [1]);
                return Task.FromResult(path);
            },
            install: (assetPath, version, ct) => Task.CompletedTask,
            persistence: new FakePersistence());

        var first = machine.CheckAsync(CancellationToken.None);
        var second = machine.CheckAsync(CancellationToken.None);
        tcs.SetResult(Meta("0.1.21"));
        await Task.WhenAll(first, second);

        Assert.Equal(1, count);
        Assert.Equal(UpdateStatus.Ready, machine.State.Status);
    }

    [Fact]
    public async Task CheckAsync_TransitionsCarryCurrentVersion()
    {
        var states = new List<UpdateState>();
        var machine = Create(Current, _ => null, transitions: states);

        await machine.CheckAsync(CancellationToken.None);

        Assert.NotEmpty(states);
        Assert.All(states, s => Assert.Equal(Current, s.Current));
        Assert.Equal(UpdateStatus.UpToDate, machine.State.Status);
    }
}
