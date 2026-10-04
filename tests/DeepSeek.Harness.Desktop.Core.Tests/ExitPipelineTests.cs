namespace DeepSeek.Harness.Desktop.Core.Tests;

/// <summary>单实例退出管道记序契约（ADR child-process-reaping-port-drift /
/// self-update-exit-reaps-dsh-child / composition-root-value-flow-pipeline）：回收三件套先于关窗、
/// 先取消监督器再回收 dsh；once-guard 使双路径重复调用为 no-op。</summary>
public class ExitPipelineTests
{
    private sealed class Recorder
    {
        public List<string> Steps { get; } = new();
        public Action Step(string name) => () => Steps.Add(name);
    }

    private static ExitPipeline Pipeline(Recorder r, bool nullListener = false) =>
        new(
            r.Step("cancel"),
            r.Step("stop"),
            r.Step("release"),
            nullListener ? null : r.Step("disposeListener"),
            r.Step("closeWindow"),
            startWatchdog: r.Step("watchdog"));

    /// <summary>验证 ReapRuntime 严格按 cancel→stop→release 次序执行：先掐断监督器恢复分支再停止宿主，marker 最后释放。</summary>
    [Fact]
    public void ReapRuntime_CancelsSupervisor_BeforeStoppingHost_BeforeReleasingMarker()
    {
        var r = new Recorder();
        Pipeline(r).ReapRuntime();

        Assert.Equal(new[] { "cancel", "stop", "release" }, r.Steps);
    }

    /// <summary>验证 OrderlyQuit 落实回收（cancel/stop/release）先于关窗、监听器释放在关窗前、看门狗最后启动的完整编排次序。</summary>
    [Fact]
    public void OrderlyQuit_ReapsBeforeDisposeAndClose_ThenWatchdog()
    {
        var r = new Recorder();
        Pipeline(r).OrderlyQuit();

        Assert.Equal(new[] { "cancel", "stop", "release", "disposeListener", "closeWindow", "watchdog" }, r.Steps);
    }

    /// <summary>验证单实例监听器未启用（dispose 为 null）时 OrderlyQuit 跳过该步仍完成其余编排、不抛异常。</summary>
    [Fact]
    public void OrderlyQuit_NullListener_DoesNotThrow()
    {
        var r = new Recorder();
        Pipeline(r, nullListener: true).OrderlyQuit();

        Assert.Equal(new[] { "cancel", "stop", "release", "closeWindow", "watchdog" }, r.Steps);
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

    /// <summary>验证 OrderlyQuit 幂等：重复调用只执行一次完整编排，不重复关窗/看门狗。</summary>
    [Fact]
    public void OrderlyQuit_SecondCall_IsNoOp()
    {
        var r = new Recorder();
        ExitPipeline pipeline = Pipeline(r);

        pipeline.OrderlyQuit();
        pipeline.OrderlyQuit();

        Assert.Equal(new[] { "cancel", "stop", "release", "disposeListener", "closeWindow", "watchdog" }, r.Steps);
    }

    /// <summary>验证有序退出后 Run 尾部再走 ReapRuntime 不重复回收（双路径收敛于同一 once-guard）。</summary>
    [Fact]
    public void OrderlyQuit_ThenRunTailReap_DoesNotRepeat()
    {
        var r = new Recorder();
        ExitPipeline pipeline = Pipeline(r);

        pipeline.OrderlyQuit();
        pipeline.ReapRuntime();

        Assert.Equal(new[] { "cancel", "stop", "release", "disposeListener", "closeWindow", "watchdog" }, r.Steps);
    }

    /// <summary>验证 Restart 完整编排：回收 → 释放监听器 → <b>先拉起新实例</b> → 关窗 → 看门狗。
    /// spawn 晚于回收是硬约束（ADR app-restart-native-switch）：旧 dsh 树不死透，看门狗 stopHost
    /// 会击杀新实例的 dsh；监听器/marker 不释放，新实例过不了单实例仲裁。</summary>
    [Fact]
    public void Restart_ReapsAndDisposes_BeforeSpawning_ThenClosesWithWatchdog()
    {
        var r = new Recorder();
        Pipeline(r).Restart(r.Step("spawn"));

        Assert.Equal(new[] { "cancel", "stop", "release", "disposeListener", "spawn", "closeWindow", "watchdog" }, r.Steps);
    }

    /// <summary>验证 Restart 幂等：重复调用只执行一次完整编排，不二次 spawn/关窗。</summary>
    [Fact]
    public void Restart_SecondCall_IsNoOp()
    {
        var r = new Recorder();
        ExitPipeline pipeline = Pipeline(r);

        pipeline.Restart(r.Step("spawn"));
        pipeline.Restart(r.Step("spawn"));

        Assert.Equal(new[] { "cancel", "stop", "release", "disposeListener", "spawn", "closeWindow", "watchdog" }, r.Steps);
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

        Assert.Equal(new[] { "cancel", "stop", "release", "disposeListener", "closeWindow", "watchdog" }, r.Steps);
        Assert.Equal(new[] { "cancel", "stop", "release", "disposeListener", "spawn", "closeWindow", "watchdog" }, r2.Steps);
    }
}
