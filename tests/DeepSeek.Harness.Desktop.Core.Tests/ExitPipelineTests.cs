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
}
