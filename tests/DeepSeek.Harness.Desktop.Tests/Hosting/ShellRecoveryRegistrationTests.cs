using DeepSeek.Harness.Desktop.Core.Ipc;
using DeepSeek.Harness.Desktop.Tests.Fakes;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>恢复/引导/监督装配（ADR avalonia-g1g3-recovery-bootstrap-supervision）：两条页面路由
/// 必须同时进路由表——引导页「重试」与恢复页「退出应用」分别依赖它们。</summary>
public class ShellRecoveryRegistrationTests
{
    /// <summary>装配产物含引导重试与恢复页退出两条路由，且各自只认自己的命令。</summary>
    [Fact]
    public void Create_RegistersBootstrapRetryAndRecoveryExit()
    {
        var log = new List<string>();
        var surface = new FakeWebViewSurface();
        var target = new AvaloniaWindowScriptTarget(surface);
        using var host = new HarnessRuntimeHost(log.Add);
        var bootstrap = new FakeBootstrap();
        var closeGate = new CloseGate();
        int closeCalls = 0;

        ShellRecoveryAssembly assembly = ShellRecoveryRegistration.Create(
            host,
            surface,
            target,
            new Uri("http://127.0.0.1:41449/"),
            bootstrap,
            new FakeDiagnosticsExport(),
            new UiLocale(),
            closeGate,
            () => null,
            () => closeCalls++,
            new RuntimeTimeouts(),
            new DshShellForward(),
            log.Add);

        Assert.Equal(2, assembly.Routers.Count);
        Assert.Contains(assembly.Routers, r => r.CanRoute(HostCommandIds.BootstrapRetry));
        Assert.Contains(assembly.Routers, r => r.CanRoute(HostCommandIds.RecoveryExit));
        Assert.DoesNotContain(assembly.Routers, r => r.CanRoute(HostCommandIds.UpdateCheck));
    }

    /// <summary>恢复页退出与托盘退出同一条顺序契约：先批准关窗闸门再关窗。</summary>
    [Fact]
    public async Task RecoveryExitRoute_ApprovesGateThenCloses()
    {
        var log = new List<string>();
        var surface = new FakeWebViewSurface();
        using var host = new HarnessRuntimeHost(log.Add);
        var closeGate = new CloseGate();
        bool closedWithGateReleased = false;

        ShellRecoveryAssembly assembly = ShellRecoveryRegistration.Create(
            host,
            surface,
            new AvaloniaWindowScriptTarget(surface),
            new Uri("http://127.0.0.1:41449/"),
            new FakeBootstrap(),
            new FakeDiagnosticsExport(),
            new UiLocale(),
            closeGate,
            () => null,
            () => closedWithGateReleased = !closeGate.ShouldCancelClose,
            new RuntimeTimeouts(),
            new DshShellForward(),
            log.Add);

        ICommandRouter router = Assert.Single(assembly.Routers, r => r.CanRoute(HostCommandIds.RecoveryExit));
        await router.RouteAsync(
            HostCommandIds.RecoveryExit,
            ReadOnlyMemory<byte>.Empty,
            new FakeServiceProvider(),
            CancellationToken.None);

        Assert.True(closedWithGateReleased);
        Assert.False(closeGate.ShouldCancelClose);
    }

    /// <summary>引导服务替身：只记录 Start 是否被调用，落定立即放行。</summary>
    private sealed class FakeBootstrap : IFirstBootBootstrap
    {
        /// <inheritdoc />
        public bool IsNeeded => false;

        /// <inheritdoc />
        public RuntimeBootstrapGate Gate { get; } = new();

        /// <summary>Start 调用次数。</summary>
        public int StartCalls { get; private set; }

        /// <summary>Cancel 调用次数。</summary>
        public int CancelCalls { get; private set; }

        /// <inheritdoc />
        public void Resolve()
        {
        }

        /// <inheritdoc />
        public void RegisterCliShim()
        {
        }

        /// <inheritdoc />
        public void Start(Func<DshWebUrl, CancellationToken, Task> onRuntimeReady) => StartCalls++;

        /// <inheritdoc />
        public void Cancel() => CancelCalls++;

        /// <inheritdoc />
        public Task<bool> WaitSettledAsync(TimeSpan? timeout, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>退出契约：`Cancel` 不阻塞（UI 线程关闭路径）、`CancelAndJoin` 按时限等待并留痕超时。</summary>
    [Fact]
    public void Context_CancelAndJoin_CancelsBothAndLogsJoinTimeout()
    {
        var log = new List<string>();
        using var host = new HarnessRuntimeHost(log.Add);
        var bootstrap = new FakeBootstrap();
        using var cts = new CancellationTokenSource();
        var context = new ShellRecoveryContext(
            bootstrap,
            host,
            cts,
            new DshShellForward(),
            new RuntimeTimeouts { SupervisorJoinTimeoutSeconds = 0 },
            log.Add);
        var release = new TaskCompletionSource();
        context.SupervisionTask = release.Task;

        context.Cancel();
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(1, bootstrap.CancelCalls);

        context.CancelAndJoin();
        Assert.Contains(log, line => line.Contains("监督循环未在时限内收口", StringComparison.Ordinal));
        release.SetResult();
    }

    /// <summary>路由分发只取参数帧，本用例不读服务容器。</summary>
    private sealed class FakeServiceProvider : IServiceProvider
    {
        /// <inheritdoc />
        public object? GetService(Type serviceType) => null;
    }
}
