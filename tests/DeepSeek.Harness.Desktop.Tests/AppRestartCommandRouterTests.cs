namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>
/// desktop.app.restart 路由行为契约（ADR app-restart-native-switch）：受理即回 {}（冲刷窗口），
/// 延迟后先 ApproveExit 再重启——记序断言 Restart 触发瞬间闸门必须已放行（与托盘退出/重启同一契约）。
/// </summary>
public class AppRestartCommandRouterTests
{
    private static ValueTask<string> RouteAsync(AppRestartCommandRouter router) =>
        router.RouteAsync(
            AppRestartCommandRouter.CommandName,
            System.Text.Encoding.UTF8.GetBytes("{}"),
            null!,
            CancellationToken.None);

    /// <summary>验证受理即回成功帧 {}，重启动作延迟到帧返回之后执行，且执行瞬间闸门已放行。</summary>
    [Fact]
    public async Task Restart_AcceptsFrame_ThenApprovesGateBeforeRestarting()
    {
        var calls = new List<string>();
        var gate = new CloseGate();
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new AppRestartCommandRouter(
            closeGate: gate,
            restart: () =>
            {
                calls.Add(gate.ShouldCancelClose ? "restart:locked" : "restart:released");
                restarted.TrySetResult();
            },
            flushDelay: TimeSpan.Zero);

        ValueTask<string> route = RouteAsync(router);
        Assert.True(route.IsCompletedSuccessfully, "受理帧必须同步返回（延迟执行是冲刷窗口的前提）");
        Assert.Equal("{}", await route);

        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "restart:released" }, calls);
        Assert.False(gate.ShouldCancelClose);
    }

    /// <summary>验证每次受理各调度一次重启：路由自身不设守卫（幂等收敛在下游 ExitPipeline 的
    /// once-guard，那里有专门回归），本契约只钉「受理与调度一一对应、不丢不重」。</summary>
    [Fact]
    public async Task Restart_Twice_SchedulesTwice_DedupDownstream()
    {
        int count = 0;
        var router = new AppRestartCommandRouter(
            closeGate: new CloseGate(),
            restart: () => Interlocked.Increment(ref count),
            flushDelay: TimeSpan.Zero);

        await RouteAsync(router);
        await RouteAsync(router);
        await Task.Delay(100);

        Assert.Equal(2, count);
    }

    /// <summary>验证非本命令名路由抛 RynCommandNotFoundException（fail loud，走宿主统一错误面）。</summary>
    [Fact]
    public void UnknownCommand_Throws()
    {
        var router = new AppRestartCommandRouter(closeGate: new CloseGate(), restart: () => { });

        Assert.Throws<Ryn.Ipc.RynCommandNotFoundException>(
            () => router.RouteAsync("desktop.other.command", ReadOnlyMemory<byte>.Empty, null!, CancellationToken.None));
        Assert.False(router.CanRoute("desktop.other.command"));
        Assert.True(router.CanRoute(AppRestartCommandRouter.CommandName));
    }
}
