using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>RuntimeBootstrapGate 记序语义 + BootstrapCommandRouter 路由契约。</summary>
public class BootstrapGateAndRouterTests
{
    /// <summary>验证 RuntimeBootstrapGate 记序往返：Signal 后 IsSignaled=true，Reset 后回到 false。</summary>
    [Fact]
    public void Gate_SignalAndReset_RoundTrip()
    {
        var gate = new RuntimeBootstrapGate();
        Assert.False(gate.IsSignaled);
        gate.Signal();
        Assert.True(gate.IsSignaled);
        gate.Reset();
        Assert.False(gate.IsSignaled);
    }

    /// <summary>验证 desktop.bootstrap.retry 可路由且同步触发闸门信号，recovery.exit 不可路由。</summary>
    [Fact]
    public void Router_RetryCommand_SignalsGate()
    {
        var gate = new RuntimeBootstrapGate();
        var router = new BootstrapCommandRouter(gate);
        Assert.True(router.CanRoute("desktop.bootstrap.retry"));
        Assert.False(router.CanRoute("desktop.recovery.exit"));
        ValueTask<string> result = router.RouteAsync("desktop.bootstrap.retry", default, null!, CancellationToken.None);
        Assert.True(result.IsCompletedSuccessfully);
        Assert.True(gate.IsSignaled);
    }

    /// <summary>验证路由未注册命令 desktop.unknown 抛 RynCommandNotFoundException。</summary>
    [Fact]
    public async Task Router_UnknownCommand_ThrowsRynCommandNotFound()
    {
        var router = new BootstrapCommandRouter(new RuntimeBootstrapGate());
        await Assert.ThrowsAsync<RynCommandNotFoundException>(
            () => router.RouteAsync("desktop.unknown", default, null!, CancellationToken.None).AsTask());
    }
}
