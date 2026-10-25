namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>单实例退出管道记序契约（ADR child-process-reaping-port-drift /
/// self-update-exit-reaps-dsh-child / composition-root-value-flow-pipeline /
/// 2026-10-25-exit-kill-focus-debounce-batch）：回收三件套与本地端口释放先于硬退/拉起继任者、
/// 先取消监督器再回收 dsh；退出终态是硬退（不投递关窗），看门狗只是硬退失败时的保险；
/// once-guard 使双路径重复调用为 no-op。</summary>
public class ExitPipelineTests
{
    private sealed class Recorder
    {
        public List<string> Steps { get; } = new();
        public Action Step(string name) => () => Steps.Add(name);
    }

    private static ExitPipeline Pipeline(Recorder r) =>
        new(
            r.Step("cancel"),
            r.Step("stop"),
            r.Step("release"),
            r.Step("ports"),
            startWatchdog: r.Step("watchdog"),
            hardExit: r.Step("hardExit"));

    /// <summary>验证 ReapRuntime 严格按 cancel→stop→release 次序执行：先掐断监督器恢复分支再停止宿主，marker 最后释放。</summary>
    [Fact]
    public void ReapRuntime_CancelsSupervisor_BeforeStoppingHost_BeforeReleasingMarker()
    {
        var r = new Recorder();
        Pipeline(r).ReapRuntime();

        Assert.Equal(new[] { "cancel", "stop", "release" }, r.Steps);
    }

    /// <summary>验证 OrderlyQuit 落实回收（cancel/stop/release）→ 释放本地端口 → 看门狗（保险）→ 硬退的
    /// 完整编排次序；终态不投递关窗（真机实证优雅拆除从不按时走完、看门狗已是事实退出路径）。</summary>
    [Fact]
    public void OrderlyQuit_ReapsAndReleasesPorts_BeforeHardExit()
    {
        var r = new Recorder();
        Pipeline(r).OrderlyQuit();

        Assert.Equal(new[] { "cancel", "stop", "release", "ports", "watchdog", "hardExit" }, r.Steps);
    }

    /// <summary>验证 ReapRuntime 幂等：重复调用只执行一次回收三件套（双路径共用单实例的收口保证）。</summary>
    [Fact]
    public void ReapRuntime_SecondCall_IsNoOp()
    {
        var r = new Recorder();
        ExitPipeline pipeline = Pipeline(r);

        pipeline.ReapRuntime();
        pipeline.ReapRuntime();

        Assert.Equal(new[] { "cancel", "stop", "release" }, r.Steps);
    }

    /// <summary>验证 OrderlyQuit 幂等：重复调用只执行一次完整编排，不重复硬退/看门狗。</summary>
    [Fact]
    public void OrderlyQuit_SecondCall_IsNoOp()
    {
        var r = new Recorder();
        ExitPipeline pipeline = Pipeline(r);

        pipeline.OrderlyQuit();
        pipeline.OrderlyQuit();

        Assert.Equal(new[] { "cancel", "stop", "release", "ports", "watchdog", "hardExit" }, r.Steps);
    }

    /// <summary>验证有序退出后 Run 尾部再走 ReapRuntime 不重复回收（双路径收敛于同一 once-guard）。</summary>
    [Fact]
    public void OrderlyQuit_ThenRunTailReap_DoesNotRepeat()
    {
        var r = new Recorder();
        ExitPipeline pipeline = Pipeline(r);

        pipeline.OrderlyQuit();
        pipeline.ReapRuntime();

        Assert.Equal(new[] { "cancel", "stop", "release", "ports", "watchdog", "hardExit" }, r.Steps);
    }

    /// <summary>验证 Restart 完整编排：回收 → 释放本地端口 → <b>先拉起新实例</b> → 看门狗（保险）→ 硬退。
    /// spawn 晚于回收与端口释放是硬约束（ADR app-restart-native-switch、restart-proxy-port-release）：
    /// 旧 dsh 树不死透，看门狗 stopHost 会击杀新实例的 dsh；单实例锁不释放，新实例过不了仲裁；
    /// 代理端口不释放，继任者绑不回记忆端口。</summary>
    [Fact]
    public void Restart_ReapsAndReleasesPorts_BeforeSpawning_ThenHardExits()
    {
        var r = new Recorder();
        Pipeline(r).Restart(r.Step("spawn"));

        Assert.Equal(new[] { "cancel", "stop", "release", "ports", "spawn", "watchdog", "hardExit" }, r.Steps);
    }

    /// <summary>验证 Restart 幂等：重复调用只执行一次完整编排，不二次 spawn/硬退。</summary>
    [Fact]
    public void Restart_SecondCall_IsNoOp()
    {
        var r = new Recorder();
        ExitPipeline pipeline = Pipeline(r);

        pipeline.Restart(r.Step("spawn"));
        pipeline.Restart(r.Step("spawn"));

        Assert.Equal(new[] { "cancel", "stop", "release", "ports", "spawn", "watchdog", "hardExit" }, r.Steps);
    }

    /// <summary>验证 Restart 与 OrderlyQuit 共享 once-guard：先到者得，后到路径整体 no-op
    /// （退出与重启竞争时只有一个编排生效）。</summary>
    [Fact]
    public void Restart_AfterOrderlyQuit_IsNoOp_AndViceVersa()
    {
        var r = new Recorder();
        ExitPipeline first = Pipeline(r);
        first.OrderlyQuit();
        first.Restart(r.Step("spawn"));

        var r2 = new Recorder();
        ExitPipeline second = Pipeline(r2);
        second.Restart(r2.Step("spawn"));
        second.OrderlyQuit();

        Assert.Equal(new[] { "cancel", "stop", "release", "ports", "watchdog", "hardExit" }, r.Steps);
        Assert.Equal(new[] { "cancel", "stop", "release", "ports", "spawn", "watchdog", "hardExit" }, r2.Steps);
    }
}
