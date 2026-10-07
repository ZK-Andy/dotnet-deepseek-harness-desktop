namespace DeepSeek.Harness.Desktop.Core.Tests.Bootstrap;

/// <summary>运行时起步用例（ADR 组合根机制收官先行批）：shim 注册→spawn 等 URL→壳铸币链经 fake 宿主/铸币单测——
/// 首启引导态跳过 spawn、无 URL 不铸币走降级、有 URL 必铸币且铸币失败仍回端点（窗口照开）。</summary>
public class RuntimeStarterTests
{
    private sealed class FakeBootstrap : IFirstBootBootstrap
    {
        public bool IsNeeded { get; init; }
        public int ShimCalls { get; private set; }
        public RuntimeBootstrapGate Gate => new();
        public PreinstallChoiceGate PreinstallGate => new();
        public void Resolve() { }
        public void RegisterCliShim() => ShimCalls++;
        public void Start(Func<DshWebUrl, CancellationToken, Task> onRuntimeReady) { }
        public void Cancel() { }
        public Task<bool> WaitSettledAsync(TimeSpan? timeout, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeHost : IRuntimeHost
    {
        public IReadOnlyList<string> StderrTail { get; init; } = Array.Empty<string>();
        public string RuntimeDescription { get; init; } = "fake dsh";
        public Uri? NextUrl { get; init; }
        public List<TimeSpan> SpawnTimeouts { get; } = [];
        public bool TryDetectUnreapableResidue() => false;
        public Task<Uri?> StartAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            SpawnTimeouts.Add(timeout);
            return Task.FromResult(NextUrl);
        }

        public Task<Uri?> RestartAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(NextUrl);
        public Task WaitForExitAsync() => Task.CompletedTask;
    }

    private static (RuntimeStarter Starter, FakeBootstrap Bootstrap, FakeHost Host, List<string> Logs, List<DshWebUrl> Minted) Setup(
        bool isNeeded = false,
        Uri? url = null,
        bool mintResult = true,
        TimeSpan? spawnTimeout = null)
    {
        var bootstrap = new FakeBootstrap { IsNeeded = isNeeded };
        var host = new FakeHost { NextUrl = url };
        var logs = new List<string>();
        var minted = new List<DshWebUrl>();
        var starter = new RuntimeStarter(
            bootstrap,
            spawnTimeout ?? TimeSpan.FromSeconds(60),
            (webUrl, log, ct) =>
            {
                minted.Add(webUrl);
                return Task.FromResult(mintResult);
            },
            logs.Add);
        return (starter, bootstrap, host, logs, minted);
    }

    /// <summary>首启引导态：spawn 与铸币均跳过、回 null；shim 注册仍执行（与搬运前语句等价）。</summary>
    [Fact]
    public async Task BootstrapNeeded_SkipsSpawnAndMint_ReturnsNull()
    {
        (RuntimeStarter starter, FakeBootstrap bootstrap, FakeHost host, List<string> logs, List<DshWebUrl> minted) =
            Setup(isNeeded: true, url: new Uri("http://127.0.0.1:9/"));

        Assert.Null(await starter.StartAsync(host, CancellationToken.None));

        Assert.Equal(1, bootstrap.ShimCalls);
        Assert.Empty(host.SpawnTimeouts);
        Assert.Empty(minted);
        Assert.Empty(logs);
    }

    /// <summary>宿主未在时限内给 URL：不铸币、回 null，stderr 只留最后 8 行（与搬运前留痕等价）。</summary>
    [Fact]
    public async Task NoUrl_LogsStderrTail_SkipsMint_ReturnsNull()
    {
        var host = new FakeHost { NextUrl = null, StderrTail = ["l0", "l1", "l2", "l3", "l4", "l5", "l6", "l7", "l8"] };
        var logs = new List<string>();
        var minted = new List<DshWebUrl>();
        var starter = new RuntimeStarter(
            new FakeBootstrap(),
            TimeSpan.FromSeconds(60),
            (webUrl, log, ct) =>
            {
                minted.Add(webUrl);
                return Task.FromResult(true);
            },
            logs.Add);

        Assert.Null(await starter.StartAsync(host, CancellationToken.None));

        Assert.Empty(minted);
        Assert.Contains(logs, line => line.Contains("降级加载 wwwroot", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("l8", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Split('\n').Contains("l0"));
    }

    /// <summary>正常起步：spawn 超时透传构造值、shim→spawn→铸币记序、回 branded 端点并留痕运行时描述与 URL。</summary>
    [Fact]
    public async Task Url_MintsAndReturnsBrandedUrl()
    {
        var bootstrap = new FakeBootstrap();
        var host = new FakeHost { NextUrl = new Uri("http://127.0.0.1:1234/") };
        var logs = new List<string>();
        var minted = new List<DshWebUrl>();
        var starter = new RuntimeStarter(
            bootstrap,
            TimeSpan.FromSeconds(77),
            (webUrl, log, ct) =>
            {
                minted.Add(webUrl);
                return Task.FromResult(true);
            },
            logs.Add);

        DshWebUrl? result = await starter.StartAsync(host, CancellationToken.None);

        Assert.Equal(DshWebUrl.From(host.NextUrl!), result);
        Assert.Equal([TimeSpan.FromSeconds(77)], host.SpawnTimeouts);
        Assert.Single(minted);
        Assert.Equal(result!.Value.Value, minted[0].Value);
        Assert.Contains(logs, line => line.Contains("runtime = fake dsh", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("dsh web = ", StringComparison.Ordinal));
    }

    /// <summary>铸币失败（loud 不抛）：仍回端点——窗口照开语义不变（转发面按错误页处理）。</summary>
    [Fact]
    public async Task MintFailure_StillReturnsUrl()
    {
        var url = new Uri("http://127.0.0.1:1234/");
        (RuntimeStarter starter, _, FakeHost host, _, _) = Setup(url: url, mintResult: false);

        Assert.Equal(DshWebUrl.From(url), await starter.StartAsync(host, CancellationToken.None));
    }
}
