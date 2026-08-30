using DeepSeek.Harness.Desktop.Services;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>有序退出编排序契约（记序回归，ADR child-process-reaping-port-drift /
/// self-update-exit-reaps-dsh-child）：回收三件套先于关窗、先取消监督器再回收 dsh。</summary>
public class ExitOrchestrationTests
{
    private sealed class Recorder
    {
        public List<string> Steps { get; } = new();
        public Action Step(string name) => () => Steps.Add(name);
    }

    /// <summary>验证 ReapRuntime 严格按 cancel→stop→release 次序执行：先掐断监督器恢复分支再停止宿主，marker 最后释放。</summary>
    [Fact]
    public void ReapRuntime_CancelsSupervisor_BeforeStoppingHost_BeforeReleasingMarker()
    {
        // 先 cancel（掐断监督器恢复分支再 spawn）再 Stop；marker 释放最后（失败不挡前两步）
        var r = new Recorder();
        ExitOrchestration.ReapRuntime(r.Step("cancel"), r.Step("stop"), r.Step("release"));

        Assert.Equal(new[] { "cancel", "stop", "release" }, r.Steps);
    }

    /// <summary>验证 OrderlyQuit 落实回收（cancel/stop/release）先于关窗、监听器释放在关窗前、看门狗最后启动的完整编排次序。</summary>
    [Fact]
    public void OrderlyQuit_ReapsBeforeDisposeAndClose_ThenWatchdog()
    {
        // 顺序契约：运行时回收先于关窗——hide-to-tray 拦截下未批准的 Close 会吞成隐藏；
        // 监听器释放在回收后、关窗前；看门狗最后启动
        var r = new Recorder();
        ExitOrchestration.OrderlyQuit(
            r.Step("cancel"),
            r.Step("stop"),
            r.Step("release"),
            r.Step("disposeListener"),
            r.Step("closeWindow"),
            r.Step("watchdog"));

        Assert.Equal(new[] { "cancel", "stop", "release", "disposeListener", "closeWindow", "watchdog" }, r.Steps);
    }

    /// <summary>验证单实例监听器未启用（dispose 为 null）时 OrderlyQuit 跳过该步仍完成其余编排、不抛异常。</summary>
    [Fact]
    public void OrderlyQuit_NullListener_DoesNotThrow()
    {
        // 单实例监听器未启用（Windows/降级）时 dispose 为 null，编排照常完成
        var r = new Recorder();
        ExitOrchestration.OrderlyQuit(
            r.Step("cancel"), r.Step("stop"), r.Step("release"),
            disposeListener: null,
            r.Step("closeWindow"),
            r.Step("watchdog"));

        Assert.Equal(new[] { "cancel", "stop", "release", "closeWindow", "watchdog" }, r.Steps);
    }
}
